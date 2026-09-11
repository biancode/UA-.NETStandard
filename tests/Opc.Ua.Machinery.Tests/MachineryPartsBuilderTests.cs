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
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Opc.Ua.ISA95.Server.Providers;
using Opc.Ua.Machinery.Energy;
using Opc.Ua.Machinery.ProcessValues;
using Opc.Ua.Machinery.Result;
using Opc.Ua.Machinery.Server;
using Opc.Ua.Machinery.Server.Builders;
using Opc.Ua.Machinery.Server.Results;
using V2 = Opc.Ua.ISA95.JobControl.V2;

namespace Opc.Ua.Machinery.Tests
{
    /// <summary>
    /// Exercises the four specialised parts of OPC 40001 through the fluent
    /// machine builder.
    /// </summary>
    [TestFixture]
    [Category("Machinery")]
    public sealed class MachineryPartsBuilderTests
    {
        [SetUp]
        public async Task SetUpAsync()
        {
            m_fixture = new MachineryServerFixture();
            await m_fixture.StartAsync();
            m_context = m_fixture.CreateBuildContext();
        }

        [TearDown]
        public async Task TearDownAsync()
        {
            if (m_fixture != null)
            {
                await m_fixture.DisposeAsync();
            }
        }

        [Test]
        public async Task ProcessValueUsesTheProcessValueVariableSubtype()
        {
            ProcessValueState? processValue = null;
            IMachineHandle<BaseObjectState> machine = await NewMachine("PV-Machine")
                .WithProcessValue(
                    new QualifiedName("Temperature"),
                    pv =>
                    {
                        processValue = pv.State;
                        pv.WithEngineeringUnits(
                                new EUInformation { DisplayName = new LocalizedText("°C") },
                                new Opc.Ua.Range { Low = -40, High = 200 })
                            .WithLimits(low: 0, high: 150)
                            .WithSetpoint(80)
                            .WithStatus();
                    })
                .BuildAsync();

            Assert.That(machine.NodeId.IsNull, Is.False);
            Assert.That(processValue, Is.Not.Null);

            var signal = processValue!.AnalogSignal as ProcessValueVariableState;
            Assert.That(
                signal,
                Is.Not.Null,
                "The instance uses OPC 40001-2's ProcessValueVariableType subtype " +
                "so the limits it adds are reachable.");
            Assert.That(signal!.LowLimit, Is.Not.Null);
            Assert.That(signal.HighLimit, Is.Not.Null);
            Assert.That(processValue.ProcessValueSetpoint, Is.Not.Null);
            Assert.That(processValue.Status, Is.Not.Null);

            QualifiedName[] units = [.. m_fixture!.Manager.ConformanceUnits];
            Assert.That(
                units,
                Contains.Item(new QualifiedName("Machinery Process Values Base Types")));
            Assert.That(
                units,
                Contains.Item(
                    new QualifiedName("Machinery Process Values Base SetpointType")));
        }

        [Test]
        public async Task JobManagementComposesTheIsa95Endpoints()
        {
            using var provider = new InMemoryIsa95JobControlProvider();
            Opc.Ua.Machinery.Jobs.JobManagementState? jobManagement = null;

            await NewMachine("Job-Machine")
                .WithJobManagement(jobs =>
                {
                    jobManagement = jobs.State;
                    jobs.WithJobOrderReceiver(provider)
                        .WithJobResponseProvider(provider)
                        .WithJobOrderCatalog(provider);
                })
                .BuildAsync();

            Assert.That(jobManagement, Is.Not.Null);
            Assert.That(jobManagement!.JobOrderControl, Is.Not.Null);
            Assert.That(jobManagement.JobOrderResults, Is.Not.Null);

            V2.ISA95JobOrderReceiverObjectState control = jobManagement.JobOrderControl!;
            Assert.That(control.Store, Is.Not.Null);
            Assert.That(control.StoreAndStart, Is.Not.Null);
            Assert.That(control.Start, Is.Not.Null);
            Assert.That(control.Stop, Is.Not.Null);
            Assert.That(control.Abort, Is.Not.Null);
            Assert.That(control.Pause, Is.Not.Null);
            Assert.That(control.Resume, Is.Not.Null);
            Assert.That(control.Clear, Is.Not.Null);
            Assert.That(control.Cancel, Is.Not.Null);
            Assert.That(control.Update, Is.Not.Null);
            Assert.That(control.RevokeStart, Is.Not.Null);
            Assert.That(
                control.Store!.OnCallAsync,
                Is.Not.Null,
                "The verbs must be bound to the ISA-95 provider.");

            QualifiedName[] units = [.. m_fixture!.Manager.ConformanceUnits];
            Assert.That(
                units,
                Contains.Item(new QualifiedName("Machinery Job Management Base")));
        }

        [Test]
        public async Task JobManagementWithoutAProviderFails()
        {
            IMachineBuilder<BaseObjectState> builder =
                NewMachine("Unbound-Job-Machine").WithJobManagement();

            ServiceResultException exception =
                Assert.ThrowsAsync<ServiceResultException>(
                    async () => await builder.BuildAsync())!;
            Assert.That(exception.Message, Does.Contain("job-order receiver"));
        }

        [Test]
        public async Task EnergyPublishesResourcesBelowConsumption()
        {
            IMachineryEnergyResourceBuilder? resource = null;
            IMachineHandle<BaseObjectState> machine = await NewMachine("Energy-Machine")
                .WithEnergy(energy => energy.AddResource(
                    MachineryEnergyCarrier.CompressedAir,
                    air =>
                    {
                        resource = air;
                        air.Main
                            .WithApplicationTag("air")
                            .WithNonElectricalEnergy(1234.5, 0)
                            .WithVolumeFlow(10f, 0.5f)
                            .WithBaseFlow(620000f, 294.65f);
                        air.AddMeteringPoint(
                            new QualifiedName("Clamping"),
                            point => point.WithNonElectricalEnergy(12.0, 0));
                    }))
                .BuildAsync();

            Assert.That(resource, Is.Not.Null);

            // OPC 40001-4 §6.1: the resource folder is a child of the
            // Consumption object of the Monitoring building block, not of the
            // machine.
            Opc.Ua.Server.ServerSystemContext context = m_fixture!.Manager.SystemContext;
            ushort machineryNamespaceIndex = NamespaceIndex(
                Opc.Ua.Machinery.Namespaces.Machinery);
            ushort energyNamespaceIndex = NamespaceIndex(
                Opc.Ua.Machinery.Energy.Namespaces.MachineryEnergy);

            NodeState? monitoring = machine.State.FindChild(
                context,
                new QualifiedName(BrowseNames.Monitoring, machineryNamespaceIndex));
            NodeState? consumption = monitoring?.FindChild(
                context,
                new QualifiedName(BrowseNames.Consumption, machineryNamespaceIndex));
            NodeState? air = consumption?.FindChild(
                context,
                new QualifiedName(
                    Opc.Ua.Machinery.Energy.BrowseNames.CompressedAir,
                    energyNamespaceIndex));
            Assert.That(air, Is.Not.Null, "The resource folder hangs below Consumption.");
            Assert.That(air, Is.SameAs(resource!.State));

            // The metering point is an OPC 34100 EnergyMeasurementType object
            // implementing INonElectricalEnergyType, not an instance of the
            // abstract interface itself.
            var main = air!.FindChild(
                context,
                new QualifiedName(
                    Opc.Ua.Machinery.Energy.BrowseNames.Main,
                    energyNamespaceIndex)) as Opc.Ua.ECM.EnergyMeasurementState;
            Assert.That(main, Is.Not.Null, "Every resource folder carries a Main point.");
            Assert.That(
                main!.TypeDefinitionId,
                Is.EqualTo(NodeId.Create(
                    Opc.Ua.ECM.ObjectTypes.EnergyMeasurementType,
                    Opc.Ua.ECM.Namespaces.ECM,
                    context.NamespaceUris)));
            Assert.That(
                main.ReferenceExists(
                    Opc.Ua.ReferenceTypeIds.HasInterface,
                    false,
                    new NodeId(
                        Opc.Ua.Machinery.Energy.ObjectTypes.INonElectricalEnergyType,
                        energyNamespaceIndex)),
                Is.True);

            ushort ecmNamespaceIndex = NamespaceIndex(Opc.Ua.ECM.Namespaces.ECM);
            var import = main.FindChild(
                context,
                new QualifiedName("NeEnergyImportHp", ecmNamespaceIndex))
                as Opc.Ua.ECM.EnergyMeasurementValueState;
            Assert.That(import, Is.Not.Null);
            Assert.That(import!.WrappedValue.TryGetValue(out double importValue), Is.True);
            Assert.That(importValue, Is.EqualTo(1234.5));

            // OPC 40001-4 §8.1: Contains runs from Main to a sub-meter whose
            // readings are part of it, and both ends are metering points.
            NodeState? clamping = air.FindChild(
                context,
                new QualifiedName("Clamping", m_fixture.Manager.MachineryInstanceNamespaceIndex));
            Assert.That(clamping, Is.Not.Null);
            var containsId = new NodeId(
                Opc.Ua.Machinery.Energy.ReferenceTypes.Contains,
                energyNamespaceIndex);
            Assert.That(
                main.ReferenceExists(containsId, false, clamping!.NodeId),
                Is.True,
                "Contains points from the main metering point at the sub-meter.");

            QualifiedName[] units = [.. m_fixture.Manager.ConformanceUnits];
            Assert.That(
                units,
                Contains.Item(new QualifiedName("Machinery Energy Base Structure")));
            Assert.That(
                units,
                Contains.Item(new QualifiedName("Machinery Energy Main grouping")));
            Assert.That(units, Contains.Item(new QualifiedName("Machinery Energy Contains")));
            Assert.That(
                units,
                Contains.Item(
                    new QualifiedName("Machinery Energy Non Electrical Volume Flow")));
            Assert.That(
                units,
                Has.No.Member(
                    new QualifiedName("Machinery Energy Non Electrical Mass Flow")),
                "Mass flow was not configured, so it must not be advertised.");

            // OPC 40001-4 composes the Machinery Monitoring Server Facet, and
            // WithEnergy materialises the monitoring add-in to satisfy it.
            string[] profiles = [.. m_fixture.Manager.ServerProfiles];
            Assert.That(
                profiles,
                Contains.Item(
                    "http://opcfoundation.org/UA-Profile/Machinery/Energy/Server/Base"));
        }

        private ushort NamespaceIndex(string namespaceUri)
        {
            return (ushort)m_fixture!.Manager.Server.NamespaceUris.GetIndex(namespaceUri);
        }

        [Test]
        public async Task ResultManagementPublishesTheOptionalMethods()
        {
            ResultManagementState? management = null;
            IMachineHandle<BaseObjectState> machine = await NewMachine("Result-Machine")
                .WithResultManagement(results =>
                {
                    management = results.State;
                    results.WithInMemoryStore(8).WithResultsFolder().WithFileTransfer();
                })
                .BuildAsync();

            Assert.That(management, Is.Not.Null);
            Assert.That(management!.GetLatestResult, Is.Not.Null);
            Assert.That(management.GetResultById, Is.Not.Null);
            Assert.That(management.GetResultIdListFiltered, Is.Not.Null);
            Assert.That(management.AcknowledgeResults, Is.Not.Null);
            Assert.That(management.ResultTransfer, Is.Not.Null);
            Assert.That(
                management.ResultTransfer!.GenerateFileForRead!.OnCall,
                Is.Not.Null,
                "The download path must be wired.");
            Assert.That(machine.Results, Is.Not.Null);
        }

        [Test]
        public async Task PublishedResultIsReturnedByGetLatestResult()
        {
            ResultManagementState? management = null;
            IMachineHandle<BaseObjectState> machine = await NewMachine("Publishing-Machine")
                .WithResultManagement(results =>
                {
                    management = results.State;
                    results.WithInMemoryStore().WithFileTransfer();
                })
                .BuildAsync();

            await machine.Results!.PublishAsync(
                new MachineryResult(
                    new ResultDataType
                    {
                        ResultMetaData = new ResultMetaDataType { ResultId = "R-1" }
                    },
                    new ByteString(Encoding.UTF8.GetBytes("payload"))));

            var outputs = new List<Variant>();
            var errors = new List<ServiceResult>();
            ServiceResult status = await management!.GetLatestResult!.CallAsync(
                m_fixture!.Manager.SystemContext,
                management.NodeId,
                new Variant[] { Variant.From(0) }.ToArrayOf(),
                errors,
                outputs);

            Assert.That(ServiceResult.IsGood(status), Is.True, status.ToString());
            Assert.That(
                outputs,
                Has.Count.EqualTo(3),
                "OPC 40001-101 returns ResultHandle, Result and Error.");
            Assert.That(outputs[0].TryGetValue(out uint resultHandle), Is.True);
            Assert.That(
                resultHandle,
                Is.GreaterThan(0),
                "A returned result must be pinned by a handle the client can release.");
            Assert.That(
                outputs[1].TryGetStructure<ResultDataType>(out ResultDataType? data),
                Is.True);
            Assert.That(data!.ResultMetaData!.ResultId, Is.EqualTo("R-1"));
            Assert.That(
                data.ResultMetaData.HasTransferableDataOnFile,
                Is.True,
                "A result with a payload must announce it.");
            Assert.That(outputs[2].TryGetValue(out int error), Is.True);
            Assert.That(error, Is.Zero);

            Assert.That(
                (QualifiedName[])[.. m_fixture.Manager.ConformanceUnits],
                Contains.Item(new QualifiedName("Machinery-Result ResultEvents")),
                "Publishing a result raises the ResultReadyEvent, and the unit " +
                "becomes advertisable only then.");

            var releaseOutputs = new List<Variant>();
            var releaseErrors = new List<ServiceResult>();
            ServiceResult released = await management.ReleaseResultHandle!.CallAsync(
                m_fixture.Manager.SystemContext,
                management.NodeId,
                new[] { Variant.From(resultHandle) }.ToArrayOf(),
                releaseErrors,
                releaseOutputs);
            Assert.That(ServiceResult.IsGood(released), Is.True, released.ToString());

            ServiceResult releasedTwice = await management.ReleaseResultHandle.CallAsync(
                m_fixture.Manager.SystemContext,
                management.NodeId,
                new[] { Variant.From(resultHandle) }.ToArrayOf(),
                releaseErrors,
                releaseOutputs);
            Assert.That(
                releasedTwice.StatusCode,
                Is.EqualTo((StatusCode)StatusCodes.BadInvalidArgument),
                "A handle can only be released once.");
        }

        [Test]
        public async Task GenerateFileForReadHandsOutAReadableFile()
        {
            ResultManagementState? management = null;
            IMachineHandle<BaseObjectState> machine = await NewMachine("Download-Machine")
                .WithResultManagement(results =>
                {
                    management = results.State;
                    results.WithInMemoryStore().WithFileTransfer();
                })
                .BuildAsync();

            byte[] payload = Encoding.UTF8.GetBytes("measurement-payload");
            await machine.Results!.PublishAsync(
                new MachineryResult(
                    new ResultDataType
                    {
                        ResultMetaData = new ResultMetaDataType { ResultId = "R-2" }
                    },
                    new ByteString(payload)));

            var options = new ResultTransferOptionsDataType { ResultId = "R-2" };
            var outputs = new List<Variant>();
            var errors = new List<ServiceResult>();
            ServiceResult status = management!.ResultTransfer!.GenerateFileForRead!.Call(
                m_fixture!.Manager.SystemContext,
                management.ResultTransfer.NodeId,
                new[] { Variant.FromStructure(options) }.ToArrayOf(),
                errors,
                outputs);

            Assert.That(ServiceResult.IsGood(status), Is.True, status.ToString());
            Assert.That(outputs, Has.Count.EqualTo(3));
            Assert.That(outputs[0].TryGetValue(out NodeId fileNodeId), Is.True);
            Assert.That(fileNodeId.IsNull, Is.False);
            Assert.That(outputs[1].TryGetValue(out uint fileHandle), Is.True);
            Assert.That(fileHandle, Is.GreaterThan(0));
            Assert.That(
                outputs[2].TryGetValue(out NodeId completion) && completion.IsNull,
                Is.True,
                "The payload is materialised synchronously, so there is no " +
                "completion state machine.");

            var file = (FileState)m_fixture.Manager.FindPredefinedNode(fileNodeId)!;
            Assert.That(file.Size!.Value, Is.EqualTo((ulong)payload.Length));

            byte[] read = ReadWholeFile(file, fileHandle, payload.Length);
            Assert.That(read, Is.EqualTo(payload));
        }

        [Test]
        public async Task DownloadOfAnUnknownResultIsRefused()
        {
            ResultManagementState? management = null;
            await NewMachine("Empty-Machine")
                .WithResultManagement(results =>
                {
                    management = results.State;
                    results.WithInMemoryStore().WithFileTransfer();
                })
                .BuildAsync();

            var options = new ResultTransferOptionsDataType { ResultId = "missing" };
            var outputs = new List<Variant>();
            var errors = new List<ServiceResult>();
            ServiceResult status = management!.ResultTransfer!.GenerateFileForRead!.Call(
                m_fixture!.Manager.SystemContext,
                management.ResultTransfer.NodeId,
                new[] { Variant.FromStructure(options) }.ToArrayOf(),
                errors,
                outputs);

            Assert.That(status.StatusCode, Is.EqualTo((StatusCode)StatusCodes.BadNotFound));
        }

        [Test]
        public async Task ResultReadyIsReportedWithAConcreteEventTypeAsync()
        {
            ResultManagementState? management = null;
            IMachineHandle<BaseObjectState> machine = await NewMachine("Event-Machine")
                .WithResultManagement(results =>
                {
                    management = results.State;
                    results.WithInMemoryStore(4).WithResultsFolder(2);
                })
                .BuildAsync();

            var events = new List<IFilterTarget>();
            NodeStateReportEventAsyncHandler? inner = management!.OnReportEventAsync;
            management.OnReportEventAsync = (context, node, e, ct) =>
            {
                events.Add(e);
                return inner?.Invoke(context, node, e, ct) ?? default;
            };

            await machine.Results!.PublishAsync(
                new MachineryResult(
                    new ResultDataType
                    {
                        ResultMetaData = new ResultMetaDataType { ResultId = "E-1" }
                    }));

            Assert.That(events, Has.Count.EqualTo(1));
            var reported = (ResultReadyEventState)events[0];
            NodeId eventTypeId = reported.EventType!.Value;
            Assert.That(eventTypeId, Is.EqualTo(reported.TypeDefinitionId));

            // OPC 40001-101 declares ResultReadyEventType abstract, and
            // OPC 10000-3 forbids instances of an abstract type: a client
            // filtering on a concrete EventType would never see the event.
            NodeState? eventType = m_fixture!.Manager.FindPredefinedNode(eventTypeId);
            Assert.That(eventType, Is.InstanceOf<BaseObjectTypeState>());
            var concrete = (BaseObjectTypeState)eventType!;
            Assert.That(concrete.IsAbstract, Is.False);
            Assert.That(
                concrete.SuperTypeId,
                Is.EqualTo(NodeId.Create(
                    Opc.Ua.Machinery.Result.ObjectTypes.ResultReadyEventType,
                    Opc.Ua.Machinery.Result.Namespaces.MachineryResult,
                    m_fixture.Manager.Server.NamespaceUris)));
            Assert.That(
                concrete.NodeId.NamespaceIndex,
                Is.EqualTo(m_fixture.Manager.MachineryInstanceNamespaceIndex));

            // The abstract type stays abstract; only the derived one is new.
            NodeState? declared = m_fixture.Manager.FindPredefinedNode(concrete.SuperTypeId);
            Assert.That(((BaseObjectTypeState)declared!).IsAbstract, Is.True);
        }

        [Test]
        public async Task PublishedResultsLandInTheResultsFolderAsync()
        {
            ResultManagementState? management = null;
            IMachineHandle<BaseObjectState> machine = await NewMachine("Variables-Machine")
                .WithResultManagement(results =>
                {
                    management = results.State;
                    results.WithInMemoryStore(4).WithResultsFolder(publishedResults: 2);
                })
                .BuildAsync();

            // OPC 40001-101's ResultVariables unit wants the Results folder to
            // carry at least one ResultType variable; an empty folder is what
            // the builder used to produce.
            var slots = new List<BaseInstanceState>();
            management!.Results!.GetChildren(m_fixture!.Manager.SystemContext, slots);
            Assert.That(slots, Has.Count.EqualTo(2));
            Assert.That(slots[0], Is.InstanceOf<ResultState>());

            await machine.Results!.PublishAsync(
                new MachineryResult(
                    new ResultDataType
                    {
                        ResultMetaData = new ResultMetaDataType { ResultId = "V-1" }
                    }));
            await machine.Results.PublishAsync(
                new MachineryResult(
                    new ResultDataType
                    {
                        ResultMetaData = new ResultMetaDataType { ResultId = "V-2" }
                    }));

            // Newest first, previous shifted down.
            Assert.That(
                ((ResultState)slots[0]).Value?.ResultMetaData?.ResultId,
                Is.EqualTo("V-2"));
            Assert.That(
                ((ResultState)slots[1]).Value?.ResultMetaData?.ResultId,
                Is.EqualTo("V-1"));

            QualifiedName[] units = [.. m_fixture.Manager.ConformanceUnits];
            Assert.That(
                units,
                Contains.Item(new QualifiedName("Machinery-Result ResultVariables")));
            Assert.That(
                (string[])[.. m_fixture.Manager.ServerProfiles],
                Contains.Item(
                    "http://opcfoundation.org/UA-Profile/Machinery/Result/Server/" +
                    "ResultTransferVariables"));
        }

        /// <summary>
        /// Reads the whole payload from the handle
        /// <c>GenerateFileForRead</c> returned. No <c>Open</c> call: OPC
        /// 10000-5 hands that handle back already open.
        /// </summary>
        private byte[] ReadWholeFile(FileState file, uint fileHandle, int length)
        {
            var readOutputs = new List<Variant>();
            var readErrors = new List<ServiceResult>();
            ServiceResult readStatus = file.Read!.Call(
                m_fixture!.Manager.SystemContext,
                file.NodeId,
                new Variant[] { Variant.From(fileHandle), Variant.From(length) }.ToArrayOf(),
                readErrors,
                readOutputs);
            Assert.That(ServiceResult.IsGood(readStatus), Is.True, readStatus.ToString());
            Assert.That(readOutputs[0].TryGetValue(out ByteString data), Is.True);
            return data.Span.ToArray();
        }

        private IMachineBuilder<BaseObjectState> NewMachine(string name)
        {
            return m_context!
                .AddMachine(new QualifiedName(name))
                .WithIdentification(id =>
                {
                    id.Manufacturer = new LocalizedText("Acme");
                    id.SerialNumber = $"SN-{name}";
                    id.ProductInstanceUri = $"urn:acme:{name}";
                });
        }

        private MachineryServerFixture? m_fixture;
        private IMachineryBuildContext? m_context;
    }
}
