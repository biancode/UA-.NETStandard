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
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Opc.Ua.Client;
using Opc.Ua.Client.Subscriptions.Streaming;
using Opc.Ua.Di.Client;
using Opc.Ua.Di.Server;
using Opc.Ua.Di.Server.Builders;
using Opc.Ua.Di.Server.Locking;
using Opc.Ua.Di.Server.SoftwareUpdate;
using Opc.Ua.Di.Server.Transfer;
using Opc.Ua.Server;
using Opc.Ua.Tests;
using ClientSession = Opc.Ua.Client.ISession;
using ServerSession = Opc.Ua.Server.ISession;

namespace Opc.Ua.Di.Tests
{
    /// <summary>
    /// Closes remaining DI Client/Server coverage gaps left cold by the
    /// feature suites: <c>ReadPropertyAsync</c>, SU method handlers
    /// (InstallFiles/Uninstall/Confirm/Prepare-fail), file-transfer edge
    /// paths, lock session-closing, transfer importer failures, and
    /// topology <c>ConnectsToParent</c>.
    /// </summary>
    [TestFixture]
    [Category("DI")]
    public sealed class DiCoverageBoostTests
    {
        private DiServerFixture m_fixture = null!;
        private MemoryPackageStore m_store = null!;

        [SetUp]
        public async Task SetUpAsync()
        {
            m_fixture = new DiServerFixture();
            await m_fixture.StartAsync().ConfigureAwait(false);
            m_store = new MemoryPackageStore();
        }

        [TearDown]
        public async Task TearDownAsync()
        {
            await m_fixture.DisposeAsync().ConfigureAwait(false);
        }

        // ------------------------------------------------------------------
        // Client — DiDeviceClient.ReadPropertyAsync
        // ------------------------------------------------------------------

        [Test]
        public async Task ReadPropertyAsyncReturnsTypedValuesAndLocalizedText()
        {
            IDeviceBuilder<DeviceState> builder = await m_fixture.Manager
                .CreateDeviceAsync(new QualifiedName(
                    "ReadPropDevice", m_fixture.Manager.DiNamespaceIndex))
                .ConfigureAwait(false);
            builder.WithIdentification(id =>
            {
                id.DeviceClass = "Pump";
                id.Manufacturer = new LocalizedText("Acme");
                id.SerialNumber = "SN-42";
            });

            Mock<ClientSession> session = DiInProcessSessionBridge.Build(m_fixture);
            var client = new DiDeviceClient(
                session.Object, builder.Device.NodeId, NullTelemetry());

            Assert.That(
                await client.ReadPropertyAsync<string>("DeviceClass").ConfigureAwait(false),
                Is.EqualTo("Pump"));
            Assert.That(
                await client.ReadPropertyAsync<string>("SerialNumber").ConfigureAwait(false),
                Is.EqualTo("SN-42"));
            Assert.That(
                await client.ReadPropertyAsync<string>("Manufacturer").ConfigureAwait(false),
                Is.EqualTo("Acme"));
            Assert.That(
                await client.ReadPropertyAsync<Guid?>("DeviceClass").ConfigureAwait(false),
                Is.Null);
            Assert.That(
                await client.ReadPropertyAsync<string>("MissingProperty").ConfigureAwait(false),
                Is.Null);
            Assert.ThrowsAsync<ArgumentNullException>(
                async () => await client.ReadPropertyAsync<string>(null!).ConfigureAwait(false));
        }

        [Test]
        public async Task ForDeviceAsyncRejectsNullTelemetry()
        {
            IDeviceBuilder<DeviceState> builder = await m_fixture.Manager
                .CreateDeviceAsync(new QualifiedName(
                    "ForDeviceTelemetry", m_fixture.Manager.DiNamespaceIndex))
                .ConfigureAwait(false);

            Mock<ClientSession> session = DiInProcessSessionBridge.Build(m_fixture);
            Assert.ThrowsAsync<ArgumentNullException>(
                async () => await DiDeviceClient.ForDeviceAsync(
                    session.Object, builder.Device.NodeId, null!).ConfigureAwait(false));
        }

        [Test]
        public async Task ForDeviceAsyncSucceedsAgainstInProcessBridge()
        {
            IDeviceBuilder<DeviceState> builder = await m_fixture.Manager
                .CreateDeviceAsync(new QualifiedName(
                    "ForDeviceOk", m_fixture.Manager.DiNamespaceIndex))
                .ConfigureAwait(false);

            Mock<ClientSession> session = DiInProcessSessionBridge.Build(m_fixture);
            DiDeviceClient client = await DiDeviceClient.ForDeviceAsync(
                session.Object, builder.Device.NodeId, NullTelemetry())
                .ConfigureAwait(false);

            Assert.That(client.DeviceNodeId, Is.EqualTo(builder.Device.NodeId));
        }

        // ------------------------------------------------------------------
        // Client — SoftwareUpdate Observe* yield-break when child absent
        // ------------------------------------------------------------------

        [Test]
        public async Task ObserveTransitionsYieldBreakWhenStateMachinesAbsent()
        {
            Mock<ClientSession> sessionMock = CreateEmptyTranslateSession();
            var client = new SoftwareUpdateClient(
                sessionMock.Object, new NodeId("su-boost", 2), NullTelemetry());
            var streaming = new Mock<IStreamingSubscription>().Object;

            Assert.That(
                await ToListAsync(client.ObservePrepareForUpdateTransitionsAsync(streaming))
                    .ConfigureAwait(false),
                Is.Empty);
            Assert.That(
                await ToListAsync(client.ObserveInstallationTransitionsAsync(streaming))
                    .ConfigureAwait(false),
                Is.Empty);
            Assert.That(
                await ToListAsync(client.ObserveConfirmationTransitionsAsync(streaming))
                    .ConfigureAwait(false),
                Is.Empty);
            Assert.That(
                await ToListAsync(client.ObservePowerCycleTransitionsAsync(streaming))
                    .ConfigureAwait(false),
                Is.Empty);
        }

        // ------------------------------------------------------------------
        // Server — SU InstallFiles / Uninstall / Confirm / Prepare-fail
        // ------------------------------------------------------------------

        [Test]
        public async Task InstallFilesSuccessAndFailurePaths()
        {
            var phases = new List<SoftwareUpdatePhase>();
            (NodeState su, InstallationStateMachineState install) =
                await CreateSuDeviceAsync(
                    "InstallFilesOk",
                    cfg => cfg
                        .OnInstall((_, _, _) => default)
                        .OnInstallationStateChanged((_, change) =>
                        {
                            phases.Add(change.Phase);
                            return default;
                        })).ConfigureAwait(false);

            ServiceResult ok = await InvokeMethodAsync(
                install.InstallFiles!, su.NodeId,
                ArrayOf.Empty<Variant>()).ConfigureAwait(false);
            Assert.That(ok, Is.EqualTo(ServiceResult.Good));
            Assert.That(phases, Is.EqualTo(
            [
                SoftwareUpdatePhase.Started,
                SoftwareUpdatePhase.Completed
            ]));

            phases.Clear();
            (NodeState suFail, InstallationStateMachineState installFail) =
                await CreateSuDeviceAsync(
                    "InstallFilesFail",
                    cfg => cfg
                        .OnInstall((_, _, _) =>
                            throw new InvalidOperationException("files boom"))
                        .OnInstallationStateChanged((_, change) =>
                        {
                            phases.Add(change.Phase);
                            return default;
                        })).ConfigureAwait(false);

            ServiceResult bad = await InvokeMethodAsync(
                installFail.InstallFiles!, suFail.NodeId,
                ArrayOf.Empty<Variant>()).ConfigureAwait(false);
            Assert.That(bad, Is.Not.EqualTo(ServiceResult.Good));
            Assert.That(phases, Is.EqualTo(
            [
                SoftwareUpdatePhase.Started,
                SoftwareUpdatePhase.Failed
            ]));
            Assert.That(installFail.CurrentState!.Value.Text, Is.EqualTo("Error"));
        }

        [Test]
        public async Task UninstallSuccessAndFailurePaths()
        {
            var phases = new List<SoftwareUpdatePhase>();
            (NodeState su, InstallationStateMachineState install) =
                await CreateSuDeviceAsync(
                    "UninstallOk",
                    cfg => cfg
                        .OnUninstall((_, _) => default)
                        .OnInstallationStateChanged((_, change) =>
                        {
                            phases.Add(change.Phase);
                            return default;
                        })).ConfigureAwait(false);

            ServiceResult ok = await InvokeMethodAsync(
                install.Uninstall!, su.NodeId,
                ArrayOf.Empty<Variant>()).ConfigureAwait(false);
            Assert.That(ok, Is.EqualTo(ServiceResult.Good));
            Assert.That(phases, Contains.Item(SoftwareUpdatePhase.Completed));

            phases.Clear();
            (NodeState suFail, InstallationStateMachineState installFail) =
                await CreateSuDeviceAsync(
                    "UninstallFail",
                    cfg => cfg
                        .OnUninstall((_, _) =>
                            throw new InvalidOperationException("uninstall boom"))
                        .OnInstallationStateChanged((_, change) =>
                        {
                            phases.Add(change.Phase);
                            return default;
                        })).ConfigureAwait(false);

            ServiceResult bad = await InvokeMethodAsync(
                installFail.Uninstall!, suFail.NodeId,
                ArrayOf.Empty<Variant>()).ConfigureAwait(false);
            Assert.That(bad, Is.Not.EqualTo(ServiceResult.Good));
            Assert.That(phases, Contains.Item(SoftwareUpdatePhase.Failed));
        }

        [Test]
        public async Task ConfirmSuccessAndFailurePaths()
        {
            var phases = new List<SoftwareUpdatePhase>();
            (NodeState su, ConfirmationStateMachineState confirm) =
                await CreateSuConfirmationAsync(
                    "ConfirmOk",
                    cfg => cfg
                        .OnConfirm((_, _, _) => default)
                        .OnConfirmStateChanged((_, change) =>
                        {
                            phases.Add(change.Phase);
                            return default;
                        })).ConfigureAwait(false);

            ServiceResult ok = await InvokeMethodAsync(
                confirm.Confirm!, su.NodeId,
                ArrayOf.Empty<Variant>()).ConfigureAwait(false);
            Assert.That(ok, Is.EqualTo(ServiceResult.Good));
            Assert.That(phases, Is.EqualTo(
            [
                SoftwareUpdatePhase.Started,
                SoftwareUpdatePhase.Completed
            ]));

            phases.Clear();
            (NodeState suFail, ConfirmationStateMachineState confirmFail) =
                await CreateSuConfirmationAsync(
                    "ConfirmFail",
                    cfg => cfg
                        .OnConfirm((_, _, _) =>
                            throw new InvalidOperationException("confirm boom"))
                        .OnConfirmStateChanged((_, change) =>
                        {
                            phases.Add(change.Phase);
                            return default;
                        })).ConfigureAwait(false);

            ServiceResult bad = await InvokeMethodAsync(
                confirmFail.Confirm!, suFail.NodeId,
                ArrayOf.Empty<Variant>()).ConfigureAwait(false);
            Assert.That(bad, Is.Not.EqualTo(ServiceResult.Good));
            Assert.That(phases, Is.EqualTo(
            [
                SoftwareUpdatePhase.Started,
                SoftwareUpdatePhase.Failed
            ]));
        }

        [Test]
        public async Task PrepareFailureMovesBackToIdle()
        {
            var phases = new List<SoftwareUpdatePhase>();
            IDeviceBuilder<DeviceState> builder = await m_fixture.Manager
                .CreateDeviceAsync(new QualifiedName(
                    "PrepareFailDevice", m_fixture.Manager.DiNamespaceIndex))
                .ConfigureAwait(false);
            builder.WithSoftwareUpdate(m_store, su => su
                .OnPrepare((_, _) => throw new InvalidOperationException("prep boom"))
                .OnPrepareStateChanged((_, change) =>
                {
                    phases.Add(change.Phase);
                    return default;
                }));

            ushort diNs = m_fixture.Manager.DiNamespaceIndex;
            ISystemContext ctx = m_fixture.Manager.SystemContext;
            NodeState suNode = builder.Device.FindChild(
                ctx, new QualifiedName("SoftwareUpdate", diNs))!;
            var prep = (PrepareForUpdateStateMachineState)suNode.FindChild(
                ctx, new QualifiedName("PrepareForUpdate", diNs))!;

            ServiceResult result = await InvokeMethodAsync(
                prep.Prepare!, suNode.NodeId,
                ArrayOf.Empty<Variant>()).ConfigureAwait(false);

            Assert.That(result, Is.Not.EqualTo(ServiceResult.Good));
            Assert.That(phases, Is.EqualTo(
            [
                SoftwareUpdatePhase.Started,
                SoftwareUpdatePhase.Failed
            ]));
            Assert.That(prep.CurrentState!.Value.Text, Is.EqualTo("Idle"));
        }

        // ------------------------------------------------------------------
        // Server — FileTransfer edge paths + Dispose
        // ------------------------------------------------------------------

        [Test]
        public async Task FileTransferCoversOpenWritePositionDisposeEdges()
        {
            IDeviceBuilder<DeviceState> builder = await m_fixture.Manager
                .CreateDeviceAsync(new QualifiedName(
                    "FtEdges", m_fixture.Manager.DiNamespaceIndex))
                .ConfigureAwait(false);
            builder.WithSoftwareUpdate(m_store);

            ushort diNs = m_fixture.Manager.DiNamespaceIndex;
            ISystemContext ctx = m_fixture.Manager.SystemContext;
            NodeState su = builder.Device.FindChild(
                ctx, new QualifiedName("SoftwareUpdate", diNs))!;
            var loading = (PackageLoadingState)su.FindChild(
                ctx, new QualifiedName("Loading", diNs))!;
            TemporaryFileTransferState ft = loading.FileTransfer!;

            NodeId fileNodeId = NodeId.Null;
            uint commitHandle = 0;
            Assert.That(
                ft.GenerateFileForWrite!.OnCall!(
                    ctx, ft.GenerateFileForWrite, ft.NodeId, Variant.Null,
                    ref fileNodeId, ref commitHandle),
                Is.EqualTo(ServiceResult.Good));

            var file = (FileState)m_fixture.Manager.FindPredefinedNode<NodeState>(fileNodeId)!;
            uint openHandle = 0;
            Assert.That(
                file.Open!.OnCall!(ctx, file.Open, file.NodeId, (byte)6, ref openHandle),
                Is.EqualTo(ServiceResult.Good));

            uint duplicate = 0;
            Assert.That(
                file.Open!.OnCall!(ctx, file.Open, file.NodeId, (byte)6, ref duplicate).StatusCode,
                Is.EqualTo(StatusCodes.BadInvalidState));

            Assert.That(
                file.Write!.OnCall!(
                    ctx, file.Write, file.NodeId, openHandle, ByteString.Empty),
                Is.EqualTo(ServiceResult.Good));

            Assert.That(
                file.Write!.OnCall!(
                    ctx, file.Write, file.NodeId, openHandle,
                    ByteString.From(new byte[] { 1, 2, 3 })),
                Is.EqualTo(ServiceResult.Good));

            ulong position = 0;
            Assert.That(
                file.GetPosition!.OnCall!(
                    ctx, file.GetPosition, file.NodeId, openHandle, ref position),
                Is.EqualTo(ServiceResult.Good));
            Assert.That(position, Is.EqualTo(3ul));

            Assert.That(
                file.SetPosition!.OnCall!(
                    ctx, file.SetPosition, file.NodeId, openHandle, 1ul),
                Is.EqualTo(ServiceResult.Good));
            Assert.That(
                file.SetPosition!.OnCall!(
                    ctx, file.SetPosition, file.NodeId, openHandle, 99ul).StatusCode,
                Is.EqualTo(StatusCodes.BadInvalidArgument));

            Assert.That(
                file.Write!.OnCall!(
                    ctx, file.Write, file.NodeId, 999u,
                    ByteString.From(new byte[] { 9 })).StatusCode,
                Is.EqualTo(StatusCodes.BadInvalidArgument));

            Assert.That(
                file.Close!.OnCall!(ctx, file.Close, file.NodeId, openHandle),
                Is.EqualTo(ServiceResult.Good));
            Assert.That(
                file.Write!.OnCall!(
                    ctx, file.Write, file.NodeId, openHandle,
                    ByteString.From(new byte[] { 4 })).StatusCode,
                Is.EqualTo(StatusCodes.BadInvalidState));
            Assert.That(
                file.Close!.OnCall!(ctx, file.Close, file.NodeId, openHandle).StatusCode,
                Is.EqualTo(StatusCodes.BadInvalidState));

            var manager = (SoftwareUpdateFileTransferManager)ft.GenerateFileForWrite.OnCall!.Target!;
            manager.Dispose();
            manager.Dispose();

            NodeId discarded = NodeId.Null;
            uint discardedHandle = 0;
            Assert.That(
                ft.GenerateFileForWrite.OnCall!(
                    ctx, ft.GenerateFileForWrite, ft.NodeId, Variant.Null,
                    ref discarded, ref discardedHandle).StatusCode,
                Is.EqualTo(StatusCodes.BadInvalidState));

            NodeId completion = NodeId.Null;
            Assert.That(
                ft.CloseAndCommit!.OnCall!(
                    ctx, ft.CloseAndCommit, ft.NodeId, commitHandle, ref completion)
                    .StatusCode,
                Is.EqualTo(StatusCodes.BadInvalidState));
        }

        [Test]
        public async Task CloseAndCommitFailsWhenPackageStoreRejects()
        {
            var rejecting = new RejectingPackageStore();
            IDeviceBuilder<DeviceState> builder = await m_fixture.Manager
                .CreateDeviceAsync(new QualifiedName(
                    "FtRejectStore", m_fixture.Manager.DiNamespaceIndex))
                .ConfigureAwait(false);
            builder.WithSoftwareUpdate(rejecting);

            ushort diNs = m_fixture.Manager.DiNamespaceIndex;
            ISystemContext ctx = m_fixture.Manager.SystemContext;
            NodeState su = builder.Device.FindChild(
                ctx, new QualifiedName("SoftwareUpdate", diNs))!;
            var loading = (PackageLoadingState)su.FindChild(
                ctx, new QualifiedName("Loading", diNs))!;
            TemporaryFileTransferState ft = loading.FileTransfer!;

            NodeId fileNodeId = NodeId.Null;
            uint commitHandle = 0;
            Assert.That(
                ft.GenerateFileForWrite!.OnCall!(
                    ctx, ft.GenerateFileForWrite, ft.NodeId, new Variant("pkg"),
                    ref fileNodeId, ref commitHandle),
                Is.EqualTo(ServiceResult.Good));

            var file = (FileState)m_fixture.Manager.FindPredefinedNode<NodeState>(fileNodeId)!;
            uint openHandle = 0;
            Assert.That(
                file.Open!.OnCall!(ctx, file.Open, file.NodeId, (byte)6, ref openHandle),
                Is.EqualTo(ServiceResult.Good));
            Assert.That(
                file.Write!.OnCall!(
                    ctx, file.Write, file.NodeId, openHandle,
                    ByteString.From(new byte[] { 1 })),
                Is.EqualTo(ServiceResult.Good));
            Assert.That(
                file.Close!.OnCall!(ctx, file.Close, file.NodeId, openHandle),
                Is.EqualTo(ServiceResult.Good));

            NodeId completion = NodeId.Null;
            ServiceResult result = ft.CloseAndCommit!.OnCall!(
                ctx, ft.CloseAndCommit, ft.NodeId, commitHandle, ref completion);
            Assert.That(result.StatusCode, Is.EqualTo(StatusCodes.BadUserAccessDenied));
        }

        // ------------------------------------------------------------------
        // Server — Lock session closing + Transfer importer failure
        // ------------------------------------------------------------------

        [Test]
        public void DefaultLockServiceReleasesLocksWhenSessionCloses()
        {
            using (var unused = new DefaultLockService())
            {
                Assert.Throws<ArgumentNullException>(
                    () => unused.AttachToSessionManager(null!));
            }

            using var service = new DefaultLockService();
            var sessionManager = new Mock<ISessionManager>();
            service.AttachToSessionManager(sessionManager.Object);
            Assert.Throws<InvalidOperationException>(
                () => service.AttachToSessionManager(sessionManager.Object));

            var elementId = new NodeId("lock-boost", 2);
            Assert.That(
                service.InitLock(TestSystemContext("alice"), elementId, "tag"),
                Is.EqualTo(LockStatus.Ok));

            var session = new Mock<ServerSession>();
            session.SetupGet(s => s.Id).Returns(new NodeId("alice", 0));
            sessionManager.Raise(
                m => m.SessionClosing += null!,
                session.Object,
                SessionEventReason.Closing);

            Assert.That(service.GetState(elementId).Locked, Is.False);

            Assert.Throws<ArgumentNullException>(() => service.GetState(NodeId.Null));
            Assert.Throws<ArgumentNullException>(
                () => service.InitLock(null!, elementId, "tag"));
            Assert.Throws<ArgumentNullException>(
                () => service.InitLock(TestSystemContext("bob"), NodeId.Null, "tag"));
        }

        [Test]
        public async Task TransferImporterExceptionIsRecordedAsFailedTransfer()
        {
            var service = new DefaultTransferService();
            var elementId = new NodeId("xfer-boost", 2);
            service.RegisterImporter(elementId, (_, _, _) =>
                throw new InvalidOperationException("import boom"));

            Assert.Throws<ArgumentNullException>(
                () => service.RegisterExporter(NodeId.Null, (_, _) => default));
            Assert.Throws<ArgumentNullException>(
                () => service.RegisterImporter(NodeId.Null, (_, _, _) => default));

            var input = new ParameterSet(elementId)
            {
                Entries =
                {
                    new ParameterEntry(
                        [new QualifiedName("a", 2)],
                        new Variant(1),
                        StatusCodes.Good)
                }
            };

            int transferId = await service.TransferToDeviceAsync(
                new SystemContext(telemetry: null!),
                elementId,
                input).ConfigureAwait(false);

            FetchResult chunk = await service.FetchAsync(
                new SystemContext(telemetry: null!),
                transferId,
                sequenceNumber: 0,
                maxResults: 10,
                omitGoodResults: false).ConfigureAwait(false);

            Assert.That(StatusCode.IsBad(chunk.TransferError), Is.True);
            Assert.ThrowsAsync<ArgumentNullException>(
                async () => await service.TransferToDeviceAsync(
                    new SystemContext(telemetry: null!),
                    NodeId.Null,
                    new ParameterSet(elementId)).ConfigureAwait(false));
        }

        // ------------------------------------------------------------------
        // Server — ConnectsToParent + DiNodeManager argument guards
        // ------------------------------------------------------------------

        [Test]
        public async Task ConnectsToParentAndDiNodeManagerGuards()
        {
            IDeviceBuilder<DeviceState> parent = await m_fixture.Manager
                .CreateDeviceAsync(new QualifiedName(
                    "ParentBoost", m_fixture.Manager.DiNamespaceIndex))
                .ConfigureAwait(false);
            IDeviceBuilder<DeviceState> child = await m_fixture.Manager
                .CreateDeviceAsync(new QualifiedName(
                    "ChildBoost", m_fixture.Manager.DiNamespaceIndex))
                .ConfigureAwait(false);

            child.ConnectsToParent(parent.Device.NodeId);
            NodeId connectsToRefType = NodeId.Create(
                global::Opc.Ua.Di.ReferenceTypes.ConnectsTo,
                DiNodeManager.DiNamespaceUri,
                m_fixture.Manager.Server.NamespaceUris);
            Assert.That(
                child.Device.ReferenceExists(
                    connectsToRefType, isInverse: true, parent.Device.NodeId),
                Is.True,
                "ConnectsToParent must add an inverse ConnectsTo reference.");

            Assert.Throws<ArgumentNullException>(
                () => ((ITopologyElementBuilder<DeviceState>)null!)
                    .ConnectsToParent(parent.Device.NodeId));
            Assert.Throws<ArgumentNullException>(
                () => child.ConnectsToParent(NodeId.Null));
            Assert.Throws<ArgumentNullException>(
                () => ((ITopologyElementBuilder<DeviceState>)null!)
                    .ConnectsTo(parent.Device.NodeId));
            Assert.Throws<ArgumentNullException>(
                () => child.ConnectsTo(NodeId.Null));

            Assert.ThrowsAsync<ArgumentNullException>(
                async () => await m_fixture.Manager
                    .CreateDeviceAsync(QualifiedName.Null).ConfigureAwait(false));
            Assert.Throws<ArgumentNullException>(
                () => m_fixture.Manager.Device<DeviceState>((DeviceState)null!));
            Assert.Throws<ArgumentNullException>(
                () => m_fixture.Manager.Device<DeviceState>(NodeId.Null));
            Assert.Throws<ServiceResultException>(
                () => m_fixture.Manager.Device<DeviceState>(new NodeId("missing", 9)));

            // Stamp a non-device ComponentState into PredefinedNodes path:
            // Device<> on a software-update child must type-mismatch.
            ushort diNs = m_fixture.Manager.DiNamespaceIndex;
            parent.WithSoftwareUpdate(m_store);
            NodeState su = parent.Device.FindChild(
                m_fixture.Manager.SystemContext,
                new QualifiedName("SoftwareUpdate", diNs))!;
            Assert.Throws<ServiceResultException>(
                () => m_fixture.Manager.Device<DeviceState>(su.NodeId));

            IDeviceBuilder<DeviceState> byId = m_fixture.Manager.Device<DeviceState>(
                parent.Device.NodeId);
            Assert.That(byId.Device.NodeId, Is.EqualTo(parent.Device.NodeId));

            parent.WithFunctionalGroup(
                new QualifiedName("CustomGroup", m_fixture.Manager.DiNamespaceIndex),
                _ => { });
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        private async Task<(NodeState Su, InstallationStateMachineState Install)>
            CreateSuDeviceAsync(string name, Action<ISoftwareUpdateBuilder> configure)
        {
            IDeviceBuilder<DeviceState> builder = await m_fixture.Manager
                .CreateDeviceAsync(new QualifiedName(
                    name, m_fixture.Manager.DiNamespaceIndex))
                .ConfigureAwait(false);
            builder.WithSoftwareUpdate(m_store, configure);

            ushort diNs = m_fixture.Manager.DiNamespaceIndex;
            ISystemContext ctx = m_fixture.Manager.SystemContext;
            NodeState su = builder.Device.FindChild(
                ctx, new QualifiedName("SoftwareUpdate", diNs))!;
            var install = (InstallationStateMachineState)su.FindChild(
                ctx, new QualifiedName("Installation", diNs))!;
            return (su, install);
        }

        private async Task<(NodeState Su, ConfirmationStateMachineState Confirm)>
            CreateSuConfirmationAsync(string name, Action<ISoftwareUpdateBuilder> configure)
        {
            IDeviceBuilder<DeviceState> builder = await m_fixture.Manager
                .CreateDeviceAsync(new QualifiedName(
                    name, m_fixture.Manager.DiNamespaceIndex))
                .ConfigureAwait(false);
            builder.WithSoftwareUpdate(m_store, configure);

            ushort diNs = m_fixture.Manager.DiNamespaceIndex;
            ISystemContext ctx = m_fixture.Manager.SystemContext;
            NodeState su = builder.Device.FindChild(
                ctx, new QualifiedName("SoftwareUpdate", diNs))!;
            var confirm = (ConfirmationStateMachineState)su.FindChild(
                ctx, new QualifiedName("Confirmation", diNs))!;
            return (su, confirm);
        }

        private static async Task<ServiceResult> InvokeMethodAsync(
            MethodState method,
            NodeId objectId,
            ArrayOf<Variant> inputs)
        {
            var outputs = new List<Variant>();
            if (method.OnCallMethod2Async is not null)
            {
                return await method.OnCallMethod2Async(
                    null!, method, objectId, inputs, outputs,
                    CancellationToken.None).ConfigureAwait(false);
            }
            if (method.OnCallMethod2 is not null)
            {
                return method.OnCallMethod2(
                    null!, method, objectId, inputs, outputs);
            }
            return ServiceResult.Good;
        }

        private static Mock<ClientSession> CreateEmptyTranslateSession()
        {
            var mock = new Mock<ClientSession>();
            var nsTable = new NamespaceTable();
            nsTable.GetIndexOrAppend(Namespaces.OpcUaDi);
            mock.SetupGet(s => s.NamespaceUris).Returns(nsTable);
            var ctx = ServiceMessageContext.Create(NullTelemetry());
            ctx.NamespaceUris = nsTable;
            mock.SetupGet(s => s.MessageContext).Returns(ctx);
            mock.Setup(s => s.TranslateBrowsePathsToNodeIdsAsync(
                    It.IsAny<RequestHeader?>(),
                    It.IsAny<ArrayOf<BrowsePath>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((RequestHeader? _, ArrayOf<BrowsePath> paths, CancellationToken _) =>
                {
                    var results = new BrowsePathResult[paths.Count];
                    for (int i = 0; i < paths.Count; i++)
                    {
                        results[i] = new BrowsePathResult
                        {
                            StatusCode = StatusCodes.BadNoMatch,
                            Targets = ArrayOf.Empty<BrowsePathTarget>()
                        };
                    }
                    return new TranslateBrowsePathsToNodeIdsResponse
                    {
                        ResponseHeader = new ResponseHeader(),
                        Results = ArrayOf.Wrapped(results),
                        DiagnosticInfos = default
                    };
                });
            return mock;
        }

        private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
        {
            var list = new List<T>();
            await foreach (T item in source.ConfigureAwait(false))
            {
                list.Add(item);
            }
            return list;
        }

        private static SystemContext TestSystemContext(string user)
        {
            return new SystemContext(telemetry: null!)
            {
                UserId = user
            };
        }

        private static ITelemetryContext NullTelemetry()
        {
            return new Mock<ITelemetryContext>().Object;
        }

        private sealed class RejectingPackageStore : ISoftwarePackageStore
        {
            public ValueTask<SoftwarePackage> AddAsync(
                SoftwarePackage metadata,
                Stream payload,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("store rejected");
            }

            public IAsyncEnumerable<SoftwarePackage> ListAsync(
                CancellationToken cancellationToken = default)
            {
                return EmptyAsync();
            }

            public ValueTask<SoftwarePackage?> GetAsync(
                string packageId,
                CancellationToken cancellationToken = default)
            {
                return new ValueTask<SoftwarePackage?>((SoftwarePackage?)null);
            }

            public ValueTask<bool> ExistsAsync(
                string packageId,
                CancellationToken cancellationToken = default)
            {
                return new ValueTask<bool>(false);
            }

            public ValueTask<Stream> OpenReadAsync(
                string packageId,
                CancellationToken cancellationToken = default)
            {
                throw new FileNotFoundException(packageId);
            }

            public ValueTask<bool> DeleteAsync(
                string packageId,
                CancellationToken cancellationToken = default)
            {
                return new ValueTask<bool>(false);
            }

            private static async IAsyncEnumerable<SoftwarePackage> EmptyAsync()
            {
                await Task.CompletedTask.ConfigureAwait(false);
                yield break;
            }
        }
    }
}
