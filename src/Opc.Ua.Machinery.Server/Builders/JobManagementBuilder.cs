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
using System.Threading;
using System.Threading.Tasks;
using Opc.Ua.ISA95.Server.Providers;
using Opc.Ua.Machinery.Jobs;
using MachineryJobsBrowseNames = Opc.Ua.Machinery.Jobs.BrowseNames;
using V2 = Opc.Ua.ISA95.JobControl.V2;

namespace Opc.Ua.Machinery.Server.Builders
{
    /// <summary>
    /// Assembles the OPC 40001-3 <c>JobManagement</c> object of a machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>JobManagementType</c> has exactly two mandatory children:
    /// <c>JobOrderControl</c>, an ISA-95 <c>JobOrderReceiverObjectType</c>, and
    /// <c>JobOrderResults</c>, an ISA-95 <c>JobResponseProviderObjectType</c>.
    /// The eleven job verbs — Store, StoreAndStart, Start, Stop, Abort, Pause,
    /// Resume, Clear, Cancel, Update, RevokeStart — belong to the ISA-95 type,
    /// not to OPC 40001-3, and are bound by the shared
    /// <see cref="Isa95JobControlV2Binder"/>.
    /// </para>
    /// <para>
    /// OPC 40001-3 needs no Device Integration of its own: it composes UA core
    /// and ISA-95 Job Control V2 only.
    /// </para>
    /// </remarks>
    public interface IJobManagementBuilder
    {
        /// <summary>
        /// Gets the job-management state being configured.
        /// </summary>
        JobManagementState State { get; }

        /// <summary>
        /// Binds the job verbs to an explicit receiver. When no receiver is
        /// bound, the builder resolves <see cref="IIsa95JobOrderReceiverV2"/>
        /// from the application services during
        /// <see cref="IMachineBuilder{TState}.BuildAsync"/>.
        /// </summary>
        /// <param name="receiver">The receiver to bind.</param>
        IJobManagementBuilder WithJobOrderReceiver(IIsa95JobOrderReceiverV2 receiver);

        /// <summary>
        /// Binds the two result-request methods to an explicit response
        /// provider. When none is bound, the builder resolves
        /// <see cref="IIsa95JobResponseProviderV2"/> from the application
        /// services.
        /// </summary>
        /// <param name="provider">The response provider to bind.</param>
        IJobManagementBuilder WithJobResponseProvider(IIsa95JobResponseProviderV2 provider);

        /// <summary>
        /// Publishes the downloadable job-order list from
        /// <paramref name="catalog"/>.
        /// </summary>
        /// <param name="catalog">The job-order catalog.</param>
        IJobManagementBuilder WithJobOrderCatalog(IIsa95JobOrderCatalog catalog);
    }

    internal sealed class JobManagementBuilder : IJobManagementBuilder
    {
        public static JobManagementBuilder Create(MachineryBuildScope scope, NodeState machine)
        {
            ushort namespaceIndex = MachineryBuilderUtilities.NamespaceIndex(
                scope.Context,
                Opc.Ua.Machinery.Jobs.Namespaces.MachineryJobs);
            JobManagementState jobManagement = MachineryBuilderUtilities.AddAddIn(
                scope.Context,
                machine,
                new QualifiedName(MachineryJobsBrowseNames.JobManagement, namespaceIndex),
                static (ctx, parent, name) =>
                    ctx.CreateInstanceOfJobManagementType(parent, name));
            scope.RecordFacet(MachineryFacet.JobManagement);
            return new JobManagementBuilder(scope, jobManagement);
        }

        private JobManagementBuilder(MachineryBuildScope scope, JobManagementState state)
        {
            m_scope = scope;
            State = state;

            m_binder = new Isa95JobControlV2Binder(
                scope.Context,
                scope.Context.NamespaceUris,
                RefreshJobOrderListAsync);

            V2.ISA95JobOrderReceiverObjectState control = State.JobOrderControl ??
                throw ServiceResultException.Create(
                    StatusCodes.BadConfigurationError,
                    "The generated JobManagementType instance is missing JobOrderControl.");
            m_binder.AddReceiverMethods(control);

            // The build only stages nodes; the actual binding runs once the
            // machine is registered, so a provider resolved from the hosting
            // container is looked up at the same point as every other service.
            scope.PostRegistrationActions.Add(BindAsync);
        }

        public JobManagementState State { get; }

        public IJobManagementBuilder WithJobOrderReceiver(IIsa95JobOrderReceiverV2 receiver)
        {
            m_scope.EnsureMutable();
            m_receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
            return this;
        }

        public IJobManagementBuilder WithJobResponseProvider(IIsa95JobResponseProviderV2 provider)
        {
            m_scope.EnsureMutable();
            m_responseProvider = provider ?? throw new ArgumentNullException(nameof(provider));
            return this;
        }

        public IJobManagementBuilder WithJobOrderCatalog(IIsa95JobOrderCatalog catalog)
        {
            m_scope.EnsureMutable();
            m_catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            return this;
        }

        private async ValueTask BindAsync(CancellationToken cancellationToken)
        {
            IIsa95JobOrderReceiverV2? receiver = m_receiver ??
                m_scope.BuildContext.GetService<IIsa95JobOrderReceiverV2>();
            if (receiver == null)
            {
                throw ServiceResultException.Create(
                    StatusCodes.BadConfigurationError,
                    "OPC 40001-3 JobManagement needs an ISA-95 Job Control V2 job-order " +
                    "receiver. Register one with AddInMemoryIsa95JobControlProvider() or " +
                    "supply it through WithJobOrderReceiver().");
            }

            V2.ISA95JobOrderReceiverObjectState control = State.JobOrderControl!;
            m_binder.BindOrderReceiver(control, receiver);
            m_binder.InitializeOrderVariables(
                control,
                m_catalog?.MaxDownloadableJobOrders ?? 0);
            control.JobOrderList!.OnSimpleReadValue = ReadJobOrderList;

            IIsa95JobResponseProviderV2? responseProvider = m_responseProvider ??
                m_scope.BuildContext.GetService<IIsa95JobResponseProviderV2>();
            if (responseProvider != null && State.JobOrderResults != null)
            {
                State.JobOrderResults.EventNotifier = EventNotifiers.SubscribeToEvents;
                m_binder.BindResponseProvider(State.JobOrderResults, responseProvider);
                m_scope.RecordFacet(MachineryFacet.JobResults);
            }

            m_catalog ??= m_scope.BuildContext.GetService<IIsa95JobOrderCatalog>();
            await RefreshJobOrderListAsync(cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask RefreshJobOrderListAsync(CancellationToken cancellationToken)
        {
            IIsa95JobOrderCatalog? catalog = m_catalog;
            if (catalog == null)
            {
                return;
            }
            long generation = Interlocked.Increment(ref m_refreshGeneration);
            ArrayOf<V2.ISA95JobOrderAndStateDataType> orders = m_binder.NormalizeJobOrders(
                await catalog.GetJobOrdersV2Async(cancellationToken).ConfigureAwait(false));
            lock (m_refreshLock)
            {
                if (generation <= m_appliedGeneration)
                {
                    return;
                }
                m_jobOrders = orders;
                m_appliedGeneration = generation;
            }
        }

        private ServiceResult ReadJobOrderList(
            ISystemContext context,
            NodeState node,
            ref Variant value)
        {
            ArrayOf<V2.ISA95JobOrderAndStateDataType> snapshot;
            lock (m_refreshLock)
            {
                snapshot = m_jobOrders;
            }
            value = Variant.FromStructure(snapshot);
            return ServiceResult.Good;
        }

        private readonly MachineryBuildScope m_scope;
        private readonly Isa95JobControlV2Binder m_binder;
        private readonly Lock m_refreshLock = new();
        private IIsa95JobOrderReceiverV2? m_receiver;
        private IIsa95JobResponseProviderV2? m_responseProvider;
        private IIsa95JobOrderCatalog? m_catalog;
        private ArrayOf<V2.ISA95JobOrderAndStateDataType> m_jobOrders = [];
        private long m_refreshGeneration;
        private long m_appliedGeneration;
    }
}
