/* ========================================================================
 * Copyright (c) 2005-2025 The OPC Foundation, Inc. All rights reserved.
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

using System.Reflection;
using NUnit.Framework;
using Opc.Ua;

namespace Opc.Ua.Di.Tests
{
    /// <summary>
    /// Tests for the source-generated Pumps model inside
    /// PumpDeviceIntegrationServer, and for the Machinery model it now consumes
    /// from Opc.Ua.Machinery. Verifies that the generator emitted the expected
    /// namespace constants and extension methods, and that neither model is
    /// loaded from an embedded NodeSet2 at runtime.
    /// </summary>
    [TestFixture]
    [Category("Pumps")]
    public sealed class PumpsNodeSetLoadingTests
    {
        [Test]
        public void MachineryNamespaceUriConstantIsEmitted()
        {
            // Machinery is no longer generated inside the sample: the sample
            // migrated off its reduced NodeSet copy onto the shared
            // Opc.Ua.Machinery package, so the constants live there.
            Assembly assembly = typeof(global::Opc.Ua.Machinery.Namespaces).Assembly;
            System.Type? ns = assembly.GetType("Opc.Ua.Machinery.Namespaces");
            Assert.That(ns, Is.Not.Null,
                "The source generator must emit Opc.Ua.Machinery.Namespaces.");
            Assert.That(
                typeof(global::Pumps.PumpNodeManager).Assembly.GetType(
                    "Opc.Ua.Machinery.Namespaces"),
                Is.Null,
                "The sample must not generate a second copy of the Machinery model.");
        }

        [Test]
        public void PumpsNamespaceUriConstantIsEmitted()
        {
            Assembly assembly = typeof(global::Pumps.PumpNodeManager).Assembly;
            System.Type? ns = assembly.GetType("Opc.Ua.Pumps.Namespaces");
            Assert.That(ns, Is.Not.Null,
                "The source generator must emit Opc.Ua.Pumps.Namespaces.");
        }

        [Test]
        public void AddOpcUaMachineryExtensionMethodIsEmitted()
        {
            Assembly assembly = typeof(global::Opc.Ua.Machinery.Namespaces).Assembly;
            System.Type? ext = assembly.GetType(
                "Opc.Ua.Machinery.OpcUaMachineryExtensions");
            Assert.That(ext, Is.Not.Null,
                "The generator must emit OpcUaMachineryExtensions.");
            MethodInfo? add = ext!.GetMethod(
                "AddOpcUaMachinery",
                [typeof(NodeStateCollection), typeof(ISystemContext)]);
            Assert.That(add, Is.Not.Null,
                "AddOpcUaMachinery(NodeStateCollection, ISystemContext) must exist.");
        }

        [Test]
        public void AddOpcUaPumpsExtensionMethodIsEmitted()
        {
            Assembly assembly = typeof(global::Pumps.PumpNodeManager).Assembly;
            System.Type? ext = assembly.GetType(
                "Opc.Ua.Pumps.OpcUaPumpsExtensions");
            Assert.That(ext, Is.Not.Null,
                "The generator must emit OpcUaPumpsExtensions.");
            MethodInfo? add = ext!.GetMethod(
                "AddOpcUaPumps",
                [typeof(NodeStateCollection), typeof(ISystemContext)]);
            Assert.That(add, Is.Not.Null,
                "AddOpcUaPumps(NodeStateCollection, ISystemContext) must exist.");
        }

        [Test]
        public void PumpServerHasNoLegacyNodeSet2EmbeddedResources()
        {
            // P2 conversion removed runtime XML loading; only the
            // source-generated extension methods remain.
            Assembly assembly = typeof(global::Pumps.PumpNodeManager).Assembly;
            string[] resources = assembly.GetManifestResourceNames();
            Assert.That(resources,
                Has.No.Member("Opc.Ua.Machinery.NodeSet2.xml"),
                "Machinery NodeSet2 must not be embedded as a resource.");
            Assert.That(resources,
                Has.No.Member("Opc.Ua.Pumps.NodeSet2.xml"),
                "Pumps NodeSet2 must not be embedded as a resource.");
        }
    }
}
