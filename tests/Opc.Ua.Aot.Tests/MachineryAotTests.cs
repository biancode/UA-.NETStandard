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

using Microsoft.Extensions.DependencyInjection;
using Opc.Ua.Machinery.Client;
using Opc.Ua.Machinery.Result;
using Opc.Ua.Machinery.Server;
using Opc.Ua.Machinery.Server.Results;

namespace Opc.Ua.Aot.Tests
{
    /// <summary>
    /// NativeAOT roots for the OPC 40001 generated models and DI surfaces.
    /// </summary>
    /// <remarks>
    /// Rooting the hosting registrations and one round-trip of each structured
    /// type the series puts on the wire is what proves the trimmer keeps the
    /// generated encoders and the reflection-free DI graph. The repository
    /// forbids AOT suppressions, so anything the linker cannot see has to fail
    /// here rather than at a customer's first publish.
    /// </remarks>
    public class MachineryAotTests
    {
        [Test]
        public async Task GeneratedModelsAndDiRegistrationAreAotSafeAsync()
        {
            var services = new ServiceCollection();
            services
                .AddOpcUa()
                .AddServer(options =>
                {
                    options.ApplicationName = "MachineryAotServer";
                    options.ApplicationUri =
                        "urn:localhost:OPCFoundation:MachineryAotServer";
                })
                .AddMachinery(options => options.Parts = MachineryParts.All);
            services
                .AddOpcUa()
                .AddClient(client =>
                {
                    client.ApplicationName = "MachineryAotClient";
                    client.ApplicationUri =
                        "urn:localhost:OPCFoundation:MachineryAotClient";
                })
                .AddMachineryClient();

            using ServiceProvider serviceProvider = services.BuildServiceProvider();
            MachineryNodeManagerFactory nodeManagerFactory =
                serviceProvider.GetService<MachineryNodeManagerFactory>();
            IMachineryModelProvider modelProvider =
                serviceProvider.GetService<IMachineryModelProvider>();
            MachineryClientFactory clientFactory =
                serviceProvider.GetService<MachineryClientFactory>();

            await Assert.That(nodeManagerFactory).IsNotNull();
            await Assert.That(modelProvider).IsNotNull();
            await Assert.That(clientFactory).IsNotNull();

            ITelemetryContext telemetry =
                serviceProvider.GetRequiredService<ITelemetryContext>();
            var messageContext = ServiceMessageContext.Create(telemetry);
            messageContext.Factory.Builder.AddOpcUaMachineryResult().Commit();

            // The one structured type OPC 40001 puts on the wire in both
            // directions: it travels out of GetLatestResult and back in as the
            // generateOptions of GenerateFileForRead.
            var result = new ResultDataType
            {
                ResultMetaData = new ResultMetaDataType { ResultId = "aot-result" }
            };
            var encoded = Variant.FromStructure(result);
            await Assert.That(encoded.TryGetStructure(out ResultDataType decoded))
                .IsTrue();
            await Assert.That(decoded.ResultMetaData.ResultId)
                .IsEqualTo(result.ResultMetaData.ResultId);

            var options = new ResultTransferOptionsDataType { ResultId = "aot-result" };
            var encodedOptions = Variant.FromStructure(options);
            await Assert.That(
                encodedOptions.TryGetStructure(
                    out ResultTransferOptionsDataType decodedOptions))
                .IsTrue();
            await Assert.That(decodedOptions.ResultId).IsEqualTo(options.ResultId);

            // Both abstract event types the series declares are observed
            // through generated record decoders, so the decoders have to
            // survive trimming as well.
            messageContext.NamespaceUris.GetIndexOrAppend(
                Opc.Ua.Machinery.Result.Namespaces.MachineryResult);
            messageContext.NamespaceUris.GetIndexOrAppend(
                Opc.Ua.Machinery.ProcessValues.Namespaces.MachineryProcessValues);
            var registry = new EventRecordDecoderRegistry();
            MachineryResultEventRecordDecoders.RegisterMachineryResultDecoders(
                registry,
                messageContext.NamespaceUris);
            Opc.Ua.Machinery.ProcessValues.MachineryProcessValuesEventRecordDecoders
                .RegisterMachineryProcessValuesDecoders(
                    registry,
                    messageContext.NamespaceUris);
            await Assert.That(registry.StandardFields.Length).IsGreaterThan(0);

            // The stand-alone result server carries no Device Integration at
            // all, so it roots a different graph.
            var resultServices = new ServiceCollection();
            resultServices
                .AddOpcUa()
                .AddServer(options2 =>
                {
                    options2.ApplicationName = "MachineryResultAotServer";
                    options2.ApplicationUri =
                        "urn:localhost:OPCFoundation:MachineryResultAotServer";
                })
                .AddMachineryResults();
            using ServiceProvider resultProvider = resultServices.BuildServiceProvider();
            await Assert.That(resultProvider.GetService<MachineryResultNodeManagerFactory>())
                .IsNotNull();
            await Assert.That(resultProvider.GetService<IMachineryResultStore>())
                .IsNotNull();
        }
    }
}
