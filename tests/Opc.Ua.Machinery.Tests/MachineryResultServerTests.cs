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
using Opc.Ua.Machinery.Result;
using Opc.Ua.Machinery.Server;
using Opc.Ua.Machinery.Server.Results;
using Opc.Ua.Server;
using Opc.Ua.Server.TestFramework;

namespace Opc.Ua.Machinery.Tests
{
    /// <summary>
    /// Covers the stand-alone OPC 40001-101 result server: the one a machine
    /// model would only get in the way of.
    /// </summary>
    [TestFixture]
    [Category("Machinery")]
    public sealed class MachineryResultServerTests
    {
        [SetUp]
        public async Task SetUpAsync()
        {
            m_fixture = new ServerFixture<StandardServer>(
                telemetry => new StandardServer(telemetry))
            {
                AutoAccept = true,
                SecurityNone = true
            };
            StandardServer server = await m_fixture.StartAsync();

            m_store = new InMemoryMachineryResultStore();
            m_manager = new MachineryResultNodeManager(
                server.CurrentInstance,
                m_fixture.Config,
                new MachineryResultServerOptions(),
                m_store);
            var externalReferences = new Dictionary<NodeId, IList<IReference>>();
            await m_manager.CreateAddressSpaceAsync(externalReferences);
        }

        [TearDown]
        public async Task TearDownAsync()
        {
            m_manager?.Dispose();
            if (m_fixture != null)
            {
                await m_fixture.StopAsync();
            }
        }

        [Test]
        public void ItCarriesNoDeviceIntegrationAndNoMachineModel()
        {
            ArrayOf<string> namespaceUris = new MachineryResultNodeManagerFactory(
                new MachineryResultServerOptions(),
                new InMemoryMachineryResultStore()).NamespacesUris;

            Assert.That(namespaceUris.ToArray(), Has.Length.EqualTo(2));
            Assert.That(
                namespaceUris.ToArray(),
                Has.No.Member(Opc.Ua.Di.Namespaces.OpcUaDi));
            Assert.That(
                namespaceUris.ToArray(),
                Has.No.Member(Opc.Ua.Machinery.Namespaces.Machinery));
        }

        [Test]
        public void ItPublishesResultManagementBelowObjects()
        {
            Assert.That(m_manager!.ResultManagement, Is.Not.Null);
            Assert.That(
                m_manager.ResultManagement!.ReferenceTypeId,
                Is.EqualTo(Opc.Ua.Types.ReferenceTypeIds.Organizes));
            Assert.That(m_manager.ResultManagement.GetLatestResult, Is.Not.Null);
            Assert.That(m_manager.ResultManagement.ResultTransfer, Is.Not.Null);
            Assert.That(m_manager.Publisher, Is.Not.Null);
        }

        [Test]
        public void ItAdvertisesResultTypesFromTheStart()
        {
            QualifiedName[] units = [.. m_manager!.ConformanceUnits];

            Assert.That(units, Contains.Item(new QualifiedName("Machinery-Result Types")));
            Assert.That(
                units,
                Has.No.Member(new QualifiedName("Machinery-Result ResultEvents")),
                "No result has been published yet.");
        }

        [Test]
        public async Task PublishingAResultEnablesTheEventUnitAndTheDownload()
        {
            byte[] payload = Encoding.UTF8.GetBytes("standalone-payload");
            await m_manager!.Publisher!.PublishAsync(
                new MachineryResult(
                    new ResultDataType
                    {
                        ResultMetaData = new ResultMetaDataType { ResultId = "S-1" }
                    },
                    new ByteString(payload)));

            QualifiedName[] units = [.. m_manager.ConformanceUnits];
            Assert.That(
                units,
                Contains.Item(new QualifiedName("Machinery-Result ResultEvents")));

            var options = new ResultTransferOptionsDataType { ResultId = "S-1" };
            var outputs = new List<Variant>();
            var errors = new List<ServiceResult>();
            ServiceResult status = m_manager.ResultManagement!.ResultTransfer!
                .GenerateFileForRead!.Call(
                    m_manager.SystemContext,
                    m_manager.ResultManagement.ResultTransfer.NodeId,
                    new[] { Variant.FromStructure(options) }.ToArrayOf(),
                    errors,
                    outputs);

            Assert.That(ServiceResult.IsGood(status), Is.True, status.ToString());
            Assert.That(outputs[0].TryGetValue(out NodeId fileNodeId), Is.True);
            var file = (FileState)m_manager.FindPredefinedNode<FileState>(fileNodeId)!;
            Assert.That(file.Size!.Value, Is.EqualTo((ulong)payload.Length));
            Assert.That(file.Writable!.Value, Is.False);
        }

        [Test]
        public void OptionsRejectARelativeInstanceNamespace()
        {
            var options = new MachineryResultServerOptions
            {
                InstanceNamespaceUri = "not-a-uri"
            };
            Assert.Throws<ArgumentException>(() =>
                _ = new MachineryResultNodeManagerFactory(
                    options,
                    new InMemoryMachineryResultStore()));
        }

        private ServerFixture<StandardServer>? m_fixture;
        private MachineryResultNodeManager? m_manager;
        private InMemoryMachineryResultStore? m_store;
    }
}
