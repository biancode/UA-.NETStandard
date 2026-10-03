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
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace Opc.Ua.Bindings.Https.WebApi.Tests.OpenApi
{
    /// <summary>
    /// Request bodies written the way the OPC Foundation's generated
    /// .NET client (<c>opcua-webapi-dotnet</c>) serializes them, posted to
    /// the REST routes. The stub's models are Newtonsoft
    /// <c>[DataMember(EmitDefaultValue = false)]</c> classes built from
    /// the same OpenAPI document, so a request omits every default-valued
    /// field, carries enumerations as integers, NodeIds as strings,
    /// DateTimes with a UTC offset, Variants as <c>{UaType, Value}</c> and
    /// ExtensionObjects as <c>{UaTypeId, UaEncoding, UaBody}</c>.
    /// </summary>
    [TestFixture]
    [Category("WebApiIntegration")]
    [SetCulture("en-us")]
    [SetUICulture("en-us")]
    public class WebApiStubInteropTests
    {
        private IHost? m_host;
        private HttpClient? m_client;
        private StubWebApiServer? m_server;

        [SetUp]
        public void SetUp()
        {
            m_server = new StubWebApiServer(StubWebApiServer.CreateFullContext());
            m_host = new HostBuilder()
                .ConfigureWebHost(webHost =>
                {
                    webHost.UseTestServer()
                        .ConfigureServices(services =>
                        {
                            services.AddSingleton<IWebApiServer>(m_server);
                            services.AddRouting();
                        });
                    webHost.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(e => e.MapWebApiEndpoints());
                    });
                })
                .Start();
            m_client = m_host.GetTestClient();
        }

        [TearDown]
        public void TearDown()
        {
            m_client?.Dispose();
            m_host?.Dispose();
        }

        [Test]
        public async Task ReadWithStubShapedBodyDecodesAsync()
        {
            using JsonDocument _ = await PostAsync("/read", """
                {
                  "RequestHeader": {
                    "Timestamp": "2026-09-23T10:15:30.1234567+02:00",
                    "RequestHandle": 17,
                    "TimeoutHint": 10000
                  },
                  "TimestampsToReturn": 2,
                  "NodesToRead": [
                    { "NodeId": "i=2258", "AttributeId": 13 },
                    { "NodeId": "nsu=http://opcfoundation.org/UA/;i=2256", "AttributeId": 13 }
                  ]
                }
                """).ConfigureAwait(false);

            var request = (ReadRequest)m_server!.LastRequest!;
            Assert.That(request.RequestHeader.RequestHandle, Is.EqualTo(17u));
            Assert.That(request.RequestHeader.TimeoutHint, Is.EqualTo(10000u));
            Assert.That(request.MaxAge, Is.Zero, "omitted MaxAge must decode as its default");
            Assert.That(request.TimestampsToReturn, Is.EqualTo(TimestampsToReturn.Both));
            Assert.That(request.NodesToRead.Count, Is.EqualTo(2));
            Assert.That(request.NodesToRead[0].NodeId, Is.EqualTo(new NodeId(2258u)));
            Assert.That(request.NodesToRead[0].AttributeId, Is.EqualTo(Attributes.Value));
            Assert.That(request.NodesToRead[1].NodeId, Is.EqualTo(new NodeId(2256u)));
        }

        [Test]
        public async Task WriteWithStubShapedDataValuesDecodesAsync()
        {
            using JsonDocument _ = await PostAsync("/write", """
                {
                  "NodesToWrite": [
                    {
                      "NodeId": "ns=1;s=Setpoint",
                      "AttributeId": 13,
                      "Value": { "UaType": 6, "Value": 42 }
                    },
                    {
                      "NodeId": "ns=1;s=Curve",
                      "AttributeId": 13,
                      "Value": { "UaType": 11, "Value": [1.5, 2.5] }
                    }
                  ]
                }
                """).ConfigureAwait(false);

            var request = (WriteRequest)m_server!.LastRequest!;
            Assert.That(request.NodesToWrite.Count, Is.EqualTo(2));
            Assert.That(request.NodesToWrite[0].NodeId, Is.EqualTo(new NodeId("Setpoint", 1)));
            Assert.That(request.NodesToWrite[0].Value.WrappedValue.TryGetValue(out int setpoint), Is.True);
            Assert.That(setpoint, Is.EqualTo(42));
            Assert.That(request.NodesToWrite[1].Value.WrappedValue.TypeInfo.BuiltInType,
                Is.EqualTo(BuiltInType.Double));
            Assert.That(request.NodesToWrite[1].Value.WrappedValue.TypeInfo.ValueRank,
                Is.EqualTo(ValueRanks.OneDimension));
        }

        [Test]
        public async Task CallWithStubShapedArgumentsDecodesAsync()
        {
            using JsonDocument _ = await PostAsync("/call", """
                {
                  "MethodsToCall": [
                    {
                      "ObjectId": "ns=1;s=Pump",
                      "MethodId": "ns=1;s=Pump.Start",
                      "InputArguments": [
                        { "UaType": 12, "Value": "fast" },
                        { "UaType": 1, "Value": true }
                      ]
                    }
                  ]
                }
                """).ConfigureAwait(false);

            var request = (CallRequest)m_server!.LastRequest!;
            CallMethodRequest method = request.MethodsToCall[0];
            Assert.That(method.MethodId, Is.EqualTo(new NodeId("Pump.Start", 1)));
            Assert.That(method.InputArguments.Count, Is.EqualTo(2));
            Assert.That(method.InputArguments[0].TryGetValue(out string speed), Is.True);
            Assert.That(speed, Is.EqualTo("fast"));
            Assert.That(method.InputArguments[1].TryGetValue(out bool enable), Is.True);
            Assert.That(enable, Is.True);
        }

        [Test]
        public async Task ActivateSessionWithBinaryIdentityTokenDecodesAsync()
        {
            // The stub's ExtensionObject model has only UaTypeId,
            // UaEncoding and UaBody, so it sends identity tokens in their
            // binary encoding: here an AnonymousIdentityToken whose
            // PolicyId is "anonymous" (Int32 length + UTF-8 bytes).
            byte[] binary = [9, 0, 0, 0, .. Encoding.UTF8.GetBytes("anonymous")];
            string typeId = ObjectIds.AnonymousIdentityToken_Encoding_DefaultBinary.ToString();

            using JsonDocument _ = await PostAsync("/activatesession", $$"""
                {
                  "RequestHeader": { "AuthenticationToken": "b=AQIDBA==" },
                  "UserIdentityToken": {
                    "UaTypeId": "{{typeId}}",
                    "UaEncoding": 1,
                    "UaBody": "{{Convert.ToBase64String(binary)}}"
                  }
                }
                """).ConfigureAwait(false);

            var request = (ActivateSessionRequest)m_server!.LastRequest!;
            Assert.That(request.RequestHeader.AuthenticationToken.IdType, Is.EqualTo(IdType.Opaque));
            Assert.That(
                request.UserIdentityToken.TryGetValue(
                    out AnonymousIdentityToken? token,
                    m_server.MessageContext),
                Is.True,
                "the binary body must decode to AnonymousIdentityToken");
            Assert.That(token!.PolicyId, Is.EqualTo("anonymous"));
        }

        [Test]
        public async Task ReadResponseHasTheShapeTheStubDeserializesAsync()
        {
            m_server!.Respond = request => new ReadResponse
            {
                ResponseHeader = new ResponseHeader
                {
                    RequestHandle = request.RequestHeader.RequestHandle
                },
                Results = new[]
                {
                    new DataValue(42),
                    DataValue.FromStatusCode(StatusCodes.BadNodeIdUnknown)
                }
            };

            using JsonDocument body = await PostAsync("/read", """
                { "RequestHeader": { "RequestHandle": 5 }, "NodesToRead": [ { "NodeId": "i=2258" } ] }
                """).ConfigureAwait(false);

            JsonElement root = body.RootElement;
            Assert.That(root.GetProperty("ResponseHeader").GetProperty("RequestHandle").GetInt64(), Is.EqualTo(5));

            // DataValue flattens its Variant: UaType and Value sit next to
            // StatusCode, which is omitted when Good.
            JsonElement good = root.GetProperty("Results")[0];
            Assert.That(good.GetProperty("UaType").GetInt32(), Is.EqualTo((int)BuiltInType.Int32));
            Assert.That(good.GetProperty("Value").GetInt32(), Is.EqualTo(42));
            Assert.That(good.TryGetProperty("StatusCode", out _), Is.False);

            // Part 6 Table 42 names the field "Status"; the OpenAPI
            // document names it "StatusCode", so a generated client reads
            // this value as Good. The encoder follows Part 6 (see
            // docs/WebApi.md, "Known specification discrepancy").
            JsonElement bad = root.GetProperty("Results")[1];
            Assert.That(
                bad.GetProperty("Status").GetProperty("Code").GetInt64(),
                Is.EqualTo((long)StatusCodes.BadNodeIdUnknown.Code));
            Assert.That(bad.TryGetProperty("StatusCode", out _), Is.False);
        }

        [Test]
        public async Task WriteKeepsStatusCodeNamedTheOpenApiWayAsync()
        {
            using JsonDocument _ = await PostAsync("/write", $$"""
                {
                  "NodesToWrite": [
                    {
                      "NodeId": "ns=1;s=Setpoint",
                      "AttributeId": 13,
                      "Value": {
                        "UaType": 6,
                        "Value": 7,
                        "StatusCode": { "Code": {{StatusCodes.UncertainLastUsableValue.Code}} }
                      }
                    }
                  ]
                }
                """).ConfigureAwait(false);

            var request = (WriteRequest)m_server!.LastRequest!;
            Assert.That(
                request.NodesToWrite[0].Value.StatusCode,
                Is.EqualTo(StatusCodes.UncertainLastUsableValue));
        }

        private async Task<JsonDocument> PostAsync(string path, string json)
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await m_client!
                .PostAsync(new Uri(path, UriKind.Relative), content)
                .ConfigureAwait(false);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), path);
            return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        }
    }
}
