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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using Opc.Ua.Bindings.WebApi;

namespace Opc.Ua.Bindings.Https.WebApi.Tests.OpenApi
{
    /// <summary>
    /// The document route and the service-set route selection,
    /// exercised over a <see cref="TestServer"/>.
    /// </summary>
    [TestFixture]
    [Category("WebApiIntegration")]
    [SetCulture("en-us")]
    [SetUICulture("en-us")]
    public class WebApiOpenApiEndpointTests
    {
        [Test]
        public async Task DocumentRouteServesAllServicesDocumentWithRelativeServerAsync()
        {
            using IHost host = StartHost(e =>
            {
                e.MapWebApiEndpoints();
                e.MapWebApiOpenApiDocument();
            });
            using HttpClient client = host.GetTestClient();

            using HttpResponseMessage response = await client
                .GetAsync(new Uri(WebApiOpenApiDocument.DefaultPath, UriKind.Relative))
                .ConfigureAwait(false);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
            using JsonDocument document = JsonDocument.Parse(
                await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            Assert.That(ServerUrls(document), Is.EqualTo(Single("/")));
            Assert.That(document.RootElement.GetProperty("paths").EnumerateObject().Count(),
                Is.EqualTo(WebApiServiceRoutes.Count));
        }

        [Test]
        public async Task DocumentRouteAdvertisesPathBaseAsync()
        {
            using IHost host = StartHost(
                e => e.MapWebApiOpenApiDocument(),
                pathBase: "/opcua");
            using HttpClient client = host.GetTestClient();

            using HttpResponseMessage response = await client
                .GetAsync(new Uri("/opcua/openapi.json", UriKind.Relative))
                .ConfigureAwait(false);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using JsonDocument document = JsonDocument.Parse(
                await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            Assert.That(ServerUrls(document), Is.EqualTo(Single("/opcua/")));
        }

        [Test]
        public async Task DocumentRouteAdvertisesConfiguredServerUrlAndPathAsync()
        {
            using IHost host = StartHost(e => e.MapWebApiOpenApiDocument(
                WebApiServiceSet.AllServices,
                "/spec/opcua.json",
                "https://plant.example:4843/"));
            using HttpClient client = host.GetTestClient();

            using HttpResponseMessage response = await client
                .GetAsync(new Uri("/spec/opcua.json", UriKind.Relative))
                .ConfigureAwait(false);

            using JsonDocument document = JsonDocument.Parse(
                await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            Assert.That(ServerUrls(document), Is.EqualTo(Single("https://plant.example:4843/")));
        }

        [Test]
        public async Task DocumentRouteTreatsAnEmptyServerUrlAsUnsetAsync()
        {
            using IHost host = StartHost(e => e.MapWebApiOpenApiDocument(serverUrl: string.Empty));
            using HttpClient client = host.GetTestClient();

            using HttpResponseMessage response = await client
                .GetAsync(new Uri(WebApiOpenApiDocument.DefaultPath, UriKind.Relative))
                .ConfigureAwait(false);

            using JsonDocument document = JsonDocument.Parse(
                await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            Assert.That(ServerUrls(document), Is.EqualTo(Single("/")));
        }

        [Test]
        public async Task SessionlessServiceSetMapsOnlySessionlessRoutesAsync()
        {
            using IHost host = StartHost(e =>
            {
                e.MapWebApiEndpoints(WebApiServiceSet.Sessionless);
                e.MapWebApiOpenApiDocument(WebApiServiceSet.Sessionless);
            });
            using HttpClient client = host.GetTestClient();

            foreach (WebApiServiceRoute route in WebApiServiceRoutes.Routes)
            {
                using var content = new ByteArrayContent(EncodeEmptyRequest(route));
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using HttpResponseMessage response = await client
                    .PostAsync(new Uri(route.Path, UriKind.Relative), content)
                    .ConfigureAwait(false);

                HttpStatusCode expected = WebApiServiceRoutes.IsSessionless(route)
                    ? HttpStatusCode.OK
                    : HttpStatusCode.NotFound;
                Assert.That(response.StatusCode, Is.EqualTo(expected), route.Path);
            }

            using HttpResponseMessage documentResponse = await client
                .GetAsync(new Uri(WebApiOpenApiDocument.DefaultPath, UriKind.Relative))
                .ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(
                await documentResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            Assert.That(
                document.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name),
                Is.EquivalentTo(WebApiServiceRoutes.SessionlessRoutes.Select(r => r.Path)));
        }

        [Test]
        public void RenderReplacesOnlyTheServersList()
        {
            using JsonDocument normative = JsonDocument.Parse(
                WebApiOpenApiDocument.GetNormativeDocument(WebApiServiceSet.AllServices).Memory);
            using JsonDocument rendered = JsonDocument.Parse(
                WebApiOpenApiDocument.Render(WebApiServiceSet.AllServices, "/").Memory);

            Assert.That(
                rendered.RootElement.EnumerateObject().Select(p => p.Name),
                Is.EqualTo(normative.RootElement.EnumerateObject().Select(p => p.Name)));
            foreach (JsonProperty property in normative.RootElement.EnumerateObject())
            {
                if (property.NameEquals("servers"))
                {
                    continue;
                }
                Assert.That(
                    Compact(rendered.RootElement.GetProperty(property.Name)),
                    Is.EqualTo(Compact(property.Value)),
                    property.Name);
            }
        }

        [Test]
        public void MappingRejectsInvalidArguments()
        {
            using IHost host = StartHost(_ => { });
            var builder = new TestEndpointRouteBuilder(host.Services);

            Assert.That(
                () => builder.MapWebApiEndpoints((WebApiServiceSet)42),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => builder.MapWebApiOpenApiDocument((WebApiServiceSet)42),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => builder.MapWebApiOpenApiDocument(pattern: string.Empty),
                Throws.ArgumentException);
            Assert.That(
                () => WebApiOpenApiDocument.Render(WebApiServiceSet.AllServices, string.Empty),
                Throws.ArgumentException);
            Assert.That(
                () => WebApiOpenApiDocument.GetNormativeDocument((WebApiServiceSet)42),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        private static string[] Single(string url)
        {
            return [url];
        }

        private static string Compact(JsonElement element)
        {
            using var buffer = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                element.WriteTo(writer);
            }
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static string[] ServerUrls(JsonDocument document)
        {
            return [.. document.RootElement
                .GetProperty("servers")
                .EnumerateArray()
                .Select(s => s.GetProperty("url").GetString()!)];
        }

        private static byte[] EncodeEmptyRequest(WebApiServiceRoute route)
        {
            var request = (IServiceRequest)Activator.CreateInstance(route.RequestType)!;
            return WebApiBodyCodec.EncodeBody(
                request,
                StubWebApiServer.Shared.MessageContext,
                WebApiMediaType.ToEncoderOptions(WebApiEncoding.Compact));
        }

        private static IHost StartHost(
            Action<IEndpointRouteBuilder> map,
            string? pathBase = null)
        {
            return new HostBuilder()
                .ConfigureWebHost(webHost =>
                {
                    webHost.UseTestServer()
                        .ConfigureServices(services =>
                        {
                            services.AddSingleton<IWebApiServer>(StubWebApiServer.Shared);
                            services.AddRouting();
                        });
                    webHost.Configure(app =>
                    {
                        if (pathBase != null)
                        {
                            app.UsePathBase(pathBase);
                        }
                        app.UseRouting();
                        app.UseEndpoints(map);
                    });
                })
                .Start();
        }

        private sealed class TestEndpointRouteBuilder : IEndpointRouteBuilder
        {
            public TestEndpointRouteBuilder(IServiceProvider services)
            {
                ServiceProvider = services;
            }

            public IServiceProvider ServiceProvider { get; }

            public System.Collections.Generic.ICollection<EndpointDataSource> DataSources { get; }
                = [];

            public IApplicationBuilder CreateApplicationBuilder()
            {
                return new ApplicationBuilder(ServiceProvider);
            }
        }
    }
}
