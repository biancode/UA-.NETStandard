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

#if NET8_0_OR_GREATER
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace Opc.Ua.Bindings.WebApi
{
    /// <summary>
    /// The service sets the OPC Foundation publishes an OpenAPI document
    /// for (<c>UA-Nodeset/OpenApi</c>). Selects both the routes the REST
    /// binding maps and the document it serves.
    /// </summary>
    public enum WebApiServiceSet
    {
        /// <summary>
        /// All 28 services of <c>opc.ua.openapi.allservices.json</c>:
        /// Discovery, Session, View, Attribute, Method, MonitoredItem and
        /// Subscription.
        /// </summary>
        AllServices = 0,

        /// <summary>
        /// The 8 services of <c>opc.ua.openapi.sessionless.json</c>
        /// (Read, Write, HistoryRead, HistoryUpdate, Call, Browse,
        /// BrowseNext, TranslateBrowsePathsToNodeIds) that a client can
        /// invoke without creating a session.
        /// </summary>
        Sessionless
    }

    /// <summary>
    /// The normative OPC UA OpenAPI documents (Part 6 §G.3, v1.05.07) as
    /// published in <c>UA-Nodeset/OpenApi</c>, embedded unchanged in this
    /// assembly, and the rendering that serves them from a REST binding.
    /// </summary>
    /// <remarks>
    /// The documents are the contract generated OpenAPI clients such as
    /// <c>opcua-webapi-dotnet</c> are built from. Serving the published
    /// bytes rather than a document derived from the CLR types keeps the
    /// advertised contract identical to the one the specification
    /// defines; the conformance tests pin the encoder to it.
    /// </remarks>
    public static class WebApiOpenApiDocument
    {
        /// <summary>
        /// Default route the REST binding serves the document at.
        /// </summary>
        public const string DefaultPath = "/openapi.json";

        /// <summary>
        /// The <c>info.version</c> of the embedded documents.
        /// </summary>
        public const string SpecificationVersion = "1.5.7";

        private const string kResourcePrefix = "Opc.Ua.Bindings.WebApi.OpenApi.";
        private const int kMaxCachedRenderings = 16;

        private static readonly Lazy<byte[]> s_allServices =
            new(() => LoadResource("opc.ua.openapi.allservices.json"));

        private static readonly Lazy<byte[]> s_sessionless =
            new(() => LoadResource("opc.ua.openapi.sessionless.json"));

        private static readonly ConcurrentDictionary<(WebApiServiceSet, string), byte[]> s_rendered
            = new();

        /// <summary>
        /// Returns the embedded document for <paramref name="serviceSet"/>
        /// byte for byte as published.
        /// </summary>
        /// <param name="serviceSet">The service set to return the document for.</param>
        /// <returns>The UTF-8 JSON document.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="serviceSet"/> is not a defined value.
        /// </exception>
        public static ByteString GetNormativeDocument(WebApiServiceSet serviceSet)
        {
            return serviceSet switch
            {
                WebApiServiceSet.AllServices => new ByteString(s_allServices.Value),
                WebApiServiceSet.Sessionless => new ByteString(s_sessionless.Value),
                _ => throw new ArgumentOutOfRangeException(nameof(serviceSet))
            };
        }

        /// <summary>
        /// Returns the document for <paramref name="serviceSet"/> with its
        /// <c>servers</c> list replaced by the single entry
        /// <paramref name="serverUrl"/>. The published documents point at
        /// <c>http://localhost:4840</c>, which is right for no deployment.
        /// </summary>
        /// <param name="serviceSet">The service set to render.</param>
        /// <param name="serverUrl">
        /// The server URL to advertise. OpenAPI 3.0 resolves a relative
        /// URL (e.g. <c>/</c>) against the location the document was
        /// served from.
        /// </param>
        /// <returns>The UTF-8 JSON document.</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="serverUrl"/> is <c>null</c> or empty.
        /// </exception>
        public static ByteString Render(WebApiServiceSet serviceSet, string serverUrl)
        {
            ArgumentException.ThrowIfNullOrEmpty(serverUrl);

            if (s_rendered.TryGetValue((serviceSet, serverUrl), out byte[]? cached))
            {
                return new ByteString(cached);
            }

            byte[] rendered = ReplaceServers(GetNormativeDocument(serviceSet).Memory, serverUrl);

            // The server URL comes from configuration or the request's
            // PathBase, never from a client-supplied header, but bound the
            // cache anyway so a misconfigured host cannot grow it.
            if (s_rendered.Count < kMaxCachedRenderings)
            {
                s_rendered.TryAdd((serviceSet, serverUrl), rendered);
            }
            return new ByteString(rendered);
        }

        private static byte[] ReplaceServers(ReadOnlyMemory<byte> document, string serverUrl)
        {
            using JsonDocument parsed = JsonDocument.Parse(document);
            using var output = new MemoryStream(document.Length + 64);
            using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                bool serversWritten = false;
                foreach (JsonProperty property in parsed.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("servers"))
                    {
                        WriteServers(writer, serverUrl);
                        serversWritten = true;
                        continue;
                    }
                    property.WriteTo(writer);

                    // OpenAPI orders servers after info; keep that order
                    // when a document omits the list entirely.
                    if (!serversWritten &&
                        property.NameEquals("info") &&
                        !parsed.RootElement.TryGetProperty("servers", out _))
                    {
                        WriteServers(writer, serverUrl);
                        serversWritten = true;
                    }
                }
                if (!serversWritten)
                {
                    WriteServers(writer, serverUrl);
                }
                writer.WriteEndObject();
            }
            return output.ToArray();
        }

        private static void WriteServers(Utf8JsonWriter writer, string serverUrl)
        {
            writer.WriteStartArray("servers");
            writer.WriteStartObject();
            writer.WriteString("url", serverUrl);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        private static byte[] LoadResource(string fileName)
        {
            using Stream stream = typeof(WebApiOpenApiDocument).Assembly
                .GetManifestResourceStream(kResourcePrefix + fileName)
                ?? throw new InvalidOperationException(
                    $"The embedded OpenAPI document '{fileName}' is missing.");
            using var buffer = new MemoryStream((int)stream.Length);
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }
}
#endif
