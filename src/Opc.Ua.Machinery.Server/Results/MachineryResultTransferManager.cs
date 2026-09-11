/* ========================================================================
 * Copyright (c) 2005-2026 The OPC Foundation, Inc. All rights reserved.
 *
 * OPC Foundation MIT License 1.00
 *
 * Permission is hereby granted, free of charge, to any person
 * obtaining a copy of this software and associated documentation
 * files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use,
 * copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following
 * conditions:
 *
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
 * OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 * WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 * OTHER DEALINGS IN THE SOFTWARE.
 *
 * The complete license agreement can be found here:
 * http://opcfoundation.org/License/MIT/1.00/
 * ======================================================================*/

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Opc.Ua.Machinery.Result;
using Opc.Ua.Server;

namespace Opc.Ua.Machinery.Server.Results
{
    /// <summary>
    /// Serves the OPC 40001-101 download path: <c>GenerateFileForRead</c> on a
    /// <c>ResultTransferType</c> object hands the caller a transient
    /// <c>FileType</c> object and a handle, and the caller reads the result
    /// payload through it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The repository already had the write half of temporary file transfer —
    /// <c>SoftwareUpdateFileTransferManager</c> serves
    /// <c>GenerateFileForWrite</c> / <c>CloseAndCommit</c> for Device
    /// Integration software packages. This is the read half, built to the same
    /// shape: one transient <c>FileState</c> per handle, the handle bound to
    /// the session that opened it, a hard cap on concurrent handles, and a
    /// timeout after which an abandoned handle is reclaimed.
    /// </para>
    /// <para>
    /// Reclaiming is swept rather than timed: it runs at the start of every
    /// <c>GenerateFileForRead</c>, which is the only moment the cap can
    /// actually bite. That keeps the manager free of a background timer per
    /// result object, at the cost of an abandoned handle surviving until the
    /// next download starts — where it costs nothing but a transient node.
    /// </para>
    /// <para>
    /// <c>GenerateFileForRead</c> returns a <c>completionStateMachine</c>
    /// NodeId for servers that generate the payload asynchronously. This
    /// manager materialises the payload synchronously from the store, so it
    /// returns <see cref="NodeId.Null"/> — the value OPC 10000-5 defines for
    /// "no completion state machine".
    /// </para>
    /// </remarks>
    internal sealed class MachineryResultTransferManager : IAsyncDisposable, IDisposable
    {
        private const byte OpenModeRead = 1;

        public MachineryResultTransferManager(
            ResultTransferState transfer,
            AsyncCustomNodeManager manager,
            IMachineryResultStore store,
            MachineryServerOptions options,
            ILogger logger,
            TimeProvider? timeProvider = null)
        {
            m_transfer = transfer ?? throw new ArgumentNullException(nameof(transfer));
            m_manager = manager ?? throw new ArgumentNullException(nameof(manager));
            m_store = store ?? throw new ArgumentNullException(nameof(store));
            m_options = options ?? throw new ArgumentNullException(nameof(options));
            m_logger = logger ?? throw new ArgumentNullException(nameof(logger));
            m_timeProvider = timeProvider ?? TimeProvider.System;

            m_transfer.ClientProcessingTimeout?.Value =
                m_options.ResultTransferTimeout.TotalMilliseconds;
            if (m_transfer.GenerateFileForRead != null)
            {
                m_transfer.GenerateFileForRead.OnCall = OnGenerateFileForRead;
            }
        }

        public void Dispose()
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        public async ValueTask DisposeAsync()
        {
            List<DownloadSlot> slots;
            lock (m_gate)
            {
                if (m_disposed)
                {
                    return;
                }
                m_disposed = true;
                slots = [.. m_slots.Values];
                m_slots.Clear();
            }
            for (int ii = 0; ii < slots.Count; ii++)
            {
                await DetachAsync(slots[ii]).ConfigureAwait(false);
            }
        }

        private ServiceResult OnGenerateFileForRead(
            ISystemContext context,
            MethodState method,
            NodeId objectId,
            Variant generateOptions,
            ref NodeId fileNodeId,
            ref uint fileHandle,
            ref NodeId completionStateMachine)
        {
            _ = method;
            _ = objectId;

            fileNodeId = NodeId.Null;
            fileHandle = 0;
            completionStateMachine = NodeId.Null;

            string? resultId = ExtractResultId(generateOptions);
            if (string.IsNullOrEmpty(resultId))
            {
                return ServiceResult.Create(
                    StatusCodes.BadInvalidArgument,
                    "GenerateFileForRead requires a ResultTransferOptionsDataType " +
                    "carrying the ResultId to download.");
            }

            MachineryResult? result = m_store
                .GetResultByIdAsync(resultId!, CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (result == null)
            {
                return ServiceResult.Create(
                    StatusCodes.BadNotFound,
                    "No result with id '{0}' is available.",
                    resultId!);
            }
            if (result.FileContent.IsNull || result.FileContent.Span.Length == 0)
            {
                return ServiceResult.Create(
                    StatusCodes.BadNothingToDo,
                    "Result '{0}' carries no transferable data on file.",
                    resultId!);
            }

            ReclaimExpiredHandles();

            DownloadSlot slot;
            lock (m_gate)
            {
                if (m_disposed)
                {
                    return StatusCodes.BadInvalidState;
                }
                if (m_slots.Count >= m_options.MaxConcurrentResultTransfers)
                {
                    return ServiceResult.Create(
                        StatusCodes.BadTooManyOperations,
                        "Concurrent result-transfer handle limit reached.");
                }

                fileHandle = ++m_nextHandle;
                slot = new DownloadSlot(
                    fileHandle,
                    SessionIdOf(context),
                    result,
                    m_timeProvider.GetUtcNow());

                // OPC 10000-5: the handle GenerateFileForRead returns is
                // already open. A client reads straight from it and closes it
                // when it is done - it never calls Open.
                slot.Open(m_timeProvider.GetUtcNow());
                try
                {
                    AttachFileState(slot);
                }
                catch (Exception ex)
                {
                    m_logger.FailedToCreateTransientResultFile(ex, fileHandle);
                    fileHandle = 0;
                    return ServiceResult.Create(
                        ex,
                        StatusCodes.BadInternalError,
                        "Failed to create the transient result FileState.");
                }
                m_slots[fileHandle] = slot;
                fileNodeId = slot.FileNodeId;
            }
            return ServiceResult.Good;
        }

        private static string? ExtractResultId(Variant generateOptions)
        {
            if (generateOptions.TryGetStructure<BaseResultTransferOptionsDataType>(
                    out BaseResultTransferOptionsDataType? options) &&
                options is not null)
            {
                return options.ResultId;
            }
            return generateOptions.TryGetValue(out string resultId) ? resultId : null;
        }

        private void AttachFileState(DownloadSlot slot)
        {
            ServerSystemContext context = m_manager.SystemContext;
            var browseName = new QualifiedName(
                $"ResultFile_{slot.Handle}",
                m_transfer.BrowseName.NamespaceIndex);

            FileState file = context.CreateInstanceOfFileType(m_transfer, browseName);
            file.SymbolicName = browseName.Name ?? string.Empty;
            file.BrowseName = browseName;
            file.DisplayName = new LocalizedText(browseName.Name);
            file.NodeId = context.NodeIdFactory!.New(context, file);
            file.ReferenceTypeId = Opc.Ua.Types.ReferenceTypeIds.HasComponent;
            file.ModellingRuleId = NodeId.Null;

            file.Writable?.Value = false;
            file.UserWritable?.Value = false;
            file.Size?.Value = (ulong)slot.Payload.Length;
            file.OpenCount?.Value = 0;
            file.MimeType?.Value = slot.Result.MimeType;
            file.Open?.OnCall = OpenSlot;
            file.Read?.OnCall = ReadSlot;
            file.Close?.OnCall = CloseSlot;
            file.GetPosition?.OnCall = GetPositionSlot;
            file.SetPosition?.OnCall = SetPositionSlot;
            file.Write?.OnCall = WriteSlot;

            m_transfer.AddChild(file);
            m_manager.AddPredefinedNodeAsync(file, CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            slot.AttachFile(file);
        }

        /// <summary>
        /// Answers <c>Open</c> on a transient result file. The handle that
        /// <c>GenerateFileForRead</c> returned is already open, so this only
        /// hands the same handle back to a client that opens defensively, and
        /// refuses any mode but read.
        /// </summary>
        private ServiceResult OpenSlot(
            ISystemContext context,
            MethodState method,
            NodeId objectId,
            byte mode,
            ref uint fileHandle)
        {
            _ = objectId;
            if (mode != OpenModeRead)
            {
                fileHandle = 0;
                return ServiceResult.Create(
                    StatusCodes.BadNotSupported,
                    "Transient result files are read-only (mode 1).");
            }
            lock (m_gate)
            {
                DownloadSlot? slot = FindSlotByFileObject(method.Parent);
                if (slot is null)
                {
                    fileHandle = 0;
                    return StatusCodes.BadNotFound;
                }
                if (!OwnsSlot(context, slot))
                {
                    fileHandle = 0;
                    return ServiceResult.Create(
                        StatusCodes.BadUserAccessDenied,
                        "File handle is owned by another session.");
                }
                slot.Seek(0, m_timeProvider.GetUtcNow());
                fileHandle = slot.Handle;
            }
            return ServiceResult.Good;
        }

        private ServiceResult ReadSlot(
            ISystemContext context,
            MethodState method,
            NodeId objectId,
            uint fileHandle,
            int length,
            ref ByteString data)
        {
            _ = method;
            _ = objectId;
            data = default;
            if (length < 0)
            {
                return ServiceResult.Create(
                    StatusCodes.BadInvalidArgument,
                    "The requested read length must not be negative.");
            }
            lock (m_gate)
            {
                if (!TryGetOwnedSlotLocked(context, fileHandle, out DownloadSlot slot, out ServiceResult err))
                {
                    return err;
                }
                if (!slot.IsOpen)
                {
                    return ServiceResult.Create(
                        StatusCodes.BadInvalidState,
                        "Transient result file is not open.");
                }
                data = slot.Read(length, m_timeProvider.GetUtcNow());
            }
            return ServiceResult.Good;
        }

        private ServiceResult WriteSlot(
            ISystemContext context,
            MethodState method,
            NodeId objectId,
            uint fileHandle,
            ByteString data)
        {
            _ = context;
            _ = method;
            _ = objectId;
            _ = fileHandle;
            _ = data;
            return ServiceResult.Create(
                StatusCodes.BadNotWritable,
                "Transient result files are read-only.");
        }

        private ServiceResult CloseSlot(
            ISystemContext context,
            MethodState method,
            NodeId objectId,
            uint fileHandle)
        {
            _ = method;
            _ = objectId;
            DownloadSlot slot;
            lock (m_gate)
            {
                if (!TryGetOwnedSlotLocked(context, fileHandle, out slot, out ServiceResult err))
                {
                    return err;
                }
                m_slots.Remove(fileHandle);
            }
            DetachAsync(slot).AsTask().GetAwaiter().GetResult();
            return ServiceResult.Good;
        }

        private ServiceResult GetPositionSlot(
            ISystemContext context,
            MethodState method,
            NodeId objectId,
            uint fileHandle,
            ref ulong position)
        {
            _ = method;
            _ = objectId;
            lock (m_gate)
            {
                if (!TryGetOwnedSlotLocked(context, fileHandle, out DownloadSlot slot, out ServiceResult err))
                {
                    position = 0;
                    return err;
                }
                position = (ulong)slot.Position;
            }
            return ServiceResult.Good;
        }

        private ServiceResult SetPositionSlot(
            ISystemContext context,
            MethodState method,
            NodeId objectId,
            uint fileHandle,
            ulong position)
        {
            _ = method;
            _ = objectId;
            lock (m_gate)
            {
                if (!TryGetOwnedSlotLocked(context, fileHandle, out DownloadSlot slot, out ServiceResult err))
                {
                    return err;
                }
                if (position > (ulong)slot.Payload.Length)
                {
                    return ServiceResult.Create(
                        StatusCodes.BadInvalidArgument,
                        "Requested position exceeds the result length.");
                }
                slot.Seek((long)position, m_timeProvider.GetUtcNow());
            }
            return ServiceResult.Good;
        }

        /// <summary>
        /// Drops handles that have seen no activity for longer than the
        /// configured timeout. Called before a new handle is minted, so the
        /// concurrency cap is measured against handles a client is still
        /// plausibly using.
        /// </summary>
        private void ReclaimExpiredHandles()
        {
            List<DownloadSlot>? expired = null;
            DateTimeOffset now = m_timeProvider.GetUtcNow();
            lock (m_gate)
            {
                foreach (KeyValuePair<uint, DownloadSlot> entry in m_slots)
                {
                    if (now - entry.Value.LastActivity > m_options.ResultTransferTimeout)
                    {
                        (expired ??= []).Add(entry.Value);
                    }
                }
                if (expired != null)
                {
                    for (int ii = 0; ii < expired.Count; ii++)
                    {
                        m_slots.Remove(expired[ii].Handle);
                    }
                }
            }
            if (expired == null)
            {
                return;
            }
            for (int ii = 0; ii < expired.Count; ii++)
            {
                m_logger.ResultTransferHandleExpired(expired[ii].Handle);
                DetachAsync(expired[ii]).AsTask().GetAwaiter().GetResult();
            }
        }

        private async ValueTask DetachAsync(DownloadSlot slot)
        {
            FileState? file = slot.DetachFile();
            if (file == null)
            {
                return;
            }
            try
            {
                await m_manager
                    .DeleteNodeAsync(m_manager.SystemContext, file.NodeId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                m_logger.FailedToRemoveTransientResultFile(ex, file.NodeId);
            }
            finally
            {
                m_transfer.RemoveChild(file);
            }
        }

        private bool TryGetOwnedSlotLocked(
            ISystemContext context,
            uint fileHandle,
            out DownloadSlot slot,
            out ServiceResult error)
        {
            if (!m_slots.TryGetValue(fileHandle, out DownloadSlot? located))
            {
                slot = null!;
                error = ServiceResult.Create(
                    StatusCodes.BadInvalidArgument,
                    "Unknown result-transfer handle.");
                return false;
            }
            if (!OwnsSlot(context, located))
            {
                slot = null!;
                error = ServiceResult.Create(
                    StatusCodes.BadUserAccessDenied,
                    "File handle is owned by another session.");
                return false;
            }
            slot = located;
            error = ServiceResult.Good;
            return true;
        }

        private static bool OwnsSlot(ISystemContext context, DownloadSlot slot)
        {
            NodeId? sessionId = SessionIdOf(context);
            return slot.OwnerSessionId == null ||
                sessionId == null ||
                slot.OwnerSessionId == sessionId;
        }

        private DownloadSlot? FindSlotByFileObject(NodeState? parent)
        {
            if (parent is null)
            {
                return null;
            }
            foreach (DownloadSlot slot in m_slots.Values)
            {
                if (ReferenceEquals(slot.FileObject, parent))
                {
                    return slot;
                }
            }
            return null;
        }

        private static NodeId? SessionIdOf(ISystemContext context)
        {
            return (context as ISessionSystemContext)?.SessionId;
        }

        private readonly ResultTransferState m_transfer;
        private readonly AsyncCustomNodeManager m_manager;
        private readonly IMachineryResultStore m_store;
        private readonly MachineryServerOptions m_options;
        private readonly ILogger m_logger;
        private readonly TimeProvider m_timeProvider;
        private readonly Lock m_gate = new();
        private readonly Dictionary<uint, DownloadSlot> m_slots = [];
        private uint m_nextHandle;
        private bool m_disposed;

        private sealed class DownloadSlot
        {
            public DownloadSlot(
                uint handle,
                NodeId? ownerSessionId,
                MachineryResult result,
                DateTimeOffset created)
            {
                Handle = handle;
                OwnerSessionId = ownerSessionId;
                Result = result;
                Payload = result.FileContent.Span.ToArray();
                LastActivity = created;
            }

            public uint Handle { get; }

            public NodeId? OwnerSessionId { get; }

            public MachineryResult Result { get; }

            public byte[] Payload { get; }

            public FileState? FileObject { get; private set; }

            public NodeId FileNodeId => FileObject?.NodeId ?? NodeId.Null;

            public bool IsOpen { get; private set; }

            public long Position { get; private set; }

            public DateTimeOffset LastActivity { get; private set; }

            public void AttachFile(FileState file)
            {
                FileObject = file;
            }

            public FileState? DetachFile()
            {
                FileState? file = FileObject;
                FileObject = null;
                IsOpen = false;
                return file;
            }

            public void Open(DateTimeOffset now)
            {
                IsOpen = true;
                Position = 0;
                LastActivity = now;
                if (FileObject?.OpenCount != null)
                {
                    FileObject.OpenCount.Value = 1;
                }
            }

            public ByteString Read(int length, DateTimeOffset now)
            {
                LastActivity = now;
                long remaining = Payload.LongLength - Position;
                if (remaining <= 0 || length == 0)
                {
                    return ByteString.Empty;
                }
                int take = (int)Math.Min(remaining, length);
                var chunk = new byte[take];
                Array.Copy(Payload, Position, chunk, 0, take);
                Position += take;
                return new ByteString(chunk);
            }

            public void Seek(long position, DateTimeOffset now)
            {
                Position = position;
                LastActivity = now;
            }
        }
    }

    internal static partial class MachineryResultTransferManagerLog
    {
        [LoggerMessage(
            EventId = MachineryServerEventIds.ResultTransferFileFailed,
            Level = LogLevel.Error,
            Message = "Failed to create the transient result file for handle {Handle}.")]
        public static partial void FailedToCreateTransientResultFile(
            this ILogger logger,
            Exception exception,
            uint handle);

        [LoggerMessage(
            EventId = MachineryServerEventIds.ResultTransferFileNotRemoved,
            Level = LogLevel.Warning,
            Message = "Failed to remove the transient result file {NodeId}.")]
        public static partial void FailedToRemoveTransientResultFile(
            this ILogger logger,
            Exception exception,
            NodeId nodeId);

        [LoggerMessage(
            EventId = MachineryServerEventIds.ResultTransferHandleExpired,
            Level = LogLevel.Information,
            Message = "Reclaimed abandoned result-transfer handle {Handle}.")]
        public static partial void ResultTransferHandleExpired(
            this ILogger logger,
            uint handle);
    }
}
