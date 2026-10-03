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
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Opc.Ua.Bindings.WebApi;

namespace Opc.Ua.Bindings.Https.WebApi.Tests.OpenApi
{
    /// <summary>
    /// Pins the REST binding to the normative OpenAPI documents embedded
    /// from <c>UA-Nodeset/OpenApi</c>: the route table, the enumerations
    /// and the JSON the encoder emits for every component schema that has
    /// a CLR counterpart. A new publication that changes the contract, or
    /// an encoder change that drifts from it, fails here.
    /// </summary>
    [TestFixture]
    [Category("WebApiOpenApiConformance")]
    [Parallelizable]
    public class WebApiOpenApiConformanceTests
    {
        private static readonly Type[] s_anchorTypes =
        [
            typeof(ReadRequest),
            typeof(NodeId),
            typeof(DataValue)
        ];

        private static readonly int[] s_sampleArray = [1, 2];
        private static readonly string[] s_part6StatusField = ["Status"];

        /// <summary>
        /// Written by dedicated JsonEncoder methods rather than as an
        /// IEncodeable; WebApiEncoderConformanceTests pins their shape.
        /// </summary>
        private static readonly HashSet<string> s_builtInSchemas =
        [
            "StatusCode",
            "LocalizedText",
            "Variant",
            "DataValue",
            "DiagnosticInfo",
            "Decimal",
            "Matrix",
            "ExtensionObject"
        ];

        [Test]
        public void EmbeddedDocumentsAreOpenApi3WithPinnedVersion(
            [Values] WebApiServiceSet serviceSet)
        {
            using JsonDocument document = Parse(serviceSet);
            JsonElement root = document.RootElement;

            Assert.That(root.GetProperty("openapi").GetString(), Does.StartWith("3.0."));
            Assert.That(
                root.GetProperty("info").GetProperty("version").GetString(),
                Is.EqualTo(WebApiOpenApiDocument.SpecificationVersion));
        }

        [Test]
        public void AllServicesDocumentMatchesRouteTable()
        {
            using JsonDocument document = Parse(WebApiServiceSet.AllServices);

            AssertPathsMatch(document, WebApiServiceRoutes.Routes);
        }

        [Test]
        public void SessionlessDocumentMatchesSessionlessRoutes()
        {
            using JsonDocument document = Parse(WebApiServiceSet.Sessionless);

            AssertPathsMatch(document, WebApiServiceRoutes.SessionlessRoutes);
            Assert.That(
                WebApiServiceRoutes.Routes.Where(WebApiServiceRoutes.IsSessionless),
                Is.EqualTo(WebApiServiceRoutes.SessionlessRoutes));
        }

        [Test]
        public void SessionlessSchemasAreIdenticalToAllServicesSchemas()
        {
            using JsonDocument all = Parse(WebApiServiceSet.AllServices);
            using JsonDocument sessionless = Parse(WebApiServiceSet.Sessionless);
            JsonElement allSchemas = Schemas(all);

            var mismatches = new List<string>();
            foreach (JsonProperty schema in Schemas(sessionless).EnumerateObject())
            {
                if (!allSchemas.TryGetProperty(schema.Name, out JsonElement counterpart))
                {
                    mismatches.Add($"{schema.Name}: missing from allservices");
                }
                else if (counterpart.GetRawText() != schema.Value.GetRawText())
                {
                    mismatches.Add($"{schema.Name}: differs from allservices");
                }
            }

            Assert.That(mismatches, Is.Empty);
        }

        [Test]
        public void EnumerationsMatchClrEnumerations()
        {
            using JsonDocument document = Parse(WebApiServiceSet.AllServices);

            var mismatches = new List<string>();
            int compared = 0;
            foreach (JsonProperty schema in Schemas(document).EnumerateObject())
            {
                // String enumerations (JsonMessageType) name the Part 14
                // JSON message headers, which have no UA enumeration.
                if (!schema.Value.TryGetProperty("enum", out JsonElement values) ||
                    !schema.Value.TryGetProperty("x-enum-varnames", out JsonElement names) ||
                    schema.Value.GetProperty("type").GetString() != "integer")
                {
                    continue;
                }
                Type? clrType = FindClrType(schema.Name) ?? FindClrType(StripBits(schema.Name));
                if (clrType == null || !clrType.IsEnum)
                {
                    mismatches.Add($"{schema.Name}: no CLR enumeration");
                    continue;
                }
                compared++;

                long[] specValues = [.. values.EnumerateArray().Select(v => v.GetInt64())];
                string[] specNames = [.. names.EnumerateArray().Select(n => n.GetString()!)];
                for (int ii = 0; ii < specNames.Length; ii++)
                {
                    if (!Enum.TryParse(clrType, specNames[ii], ignoreCase: false, out object? member))
                    {
                        mismatches.Add($"{schema.Name}.{specNames[ii]}: no CLR member");
                        continue;
                    }
                    long clrValue = Convert.ToInt64(member, System.Globalization.CultureInfo.InvariantCulture);
                    if (clrValue != specValues[ii])
                    {
                        mismatches.Add(
                            $"{schema.Name}.{specNames[ii]}: spec {specValues[ii]}, CLR {clrValue}");
                    }
                }
            }

            Assert.That(compared, Is.GreaterThan(10), "too few enumerations were compared");
            Assert.That(mismatches, Is.Empty);
        }

        [Test]
        public void EncodedStructuresUseOnlySchemaProperties(
            [Values] WebApiEncoding encoding)
        {
            using JsonDocument document = Parse(WebApiServiceSet.AllServices);
            JsonElement schemas = Schemas(document);
            ServiceMessageContext context = ServiceMessageContext.CreateEmpty(new TestTelemetryContext());
            JsonEncoderOptions options = WebApiMediaType.ToEncoderOptions(encoding);

            var mismatches = new List<string>();
            int compared = 0;
            foreach (JsonProperty schema in schemas.EnumerateObject())
            {
                if (!schema.Value.TryGetProperty("properties", out _) ||
                    s_builtInSchemas.Contains(schema.Name))
                {
                    continue;
                }
                Type? clrType = FindClrType(schema.Name);
                if (clrType == null ||
                    !typeof(IEncodeable).IsAssignableFrom(clrType) ||
                    clrType.IsAbstract ||
                    (clrType.GetConstructor(Type.EmptyTypes) == null && !clrType.IsValueType))
                {
                    mismatches.Add($"{schema.Name}: no constructible CLR structure");
                    continue;
                }
                compared++;

                HashSet<string> allowed = CollectProperties(schemas, schema.Value);
                var instance = (IEncodeable)Activator.CreateInstance(clrType)!;
                byte[] json = WebApiBodyCodec.EncodeBody(instance, context, options);
                using JsonDocument encoded = JsonDocument.Parse(json);
                foreach (JsonProperty property in encoded.RootElement.EnumerateObject())
                {
                    if (!allowed.Contains(property.Name))
                    {
                        mismatches.Add($"{schema.Name}.{property.Name}: not in schema");
                    }
                }
            }

            Assert.That(compared, Is.GreaterThan(100), "too few structures were compared");
            Assert.That(mismatches, Is.Empty);
        }

        [Test]
        public void VerboseEncodingEmitsEverySchemaProperty()
        {
            using JsonDocument document = Parse(WebApiServiceSet.AllServices);
            JsonElement schemas = Schemas(document);
            ServiceMessageContext context = ServiceMessageContext.CreateEmpty(new TestTelemetryContext());
            JsonEncoderOptions options = WebApiMediaType.ToEncoderOptions(WebApiEncoding.Verbose);

            var mismatches = new List<string>();
            int compared = 0;
            foreach (JsonProperty schema in schemas.EnumerateObject())
            {
                if (!schema.Value.TryGetProperty("properties", out _) ||
                    s_builtInSchemas.Contains(schema.Name) ||
                    FindClrType(schema.Name) is not Type clrType ||
                    !typeof(IEncodeable).IsAssignableFrom(clrType) ||
                    clrType.IsAbstract ||
                    (clrType.GetConstructor(Type.EmptyTypes) == null && !clrType.IsValueType))
                {
                    continue;
                }
                compared++;

                var instance = (IEncodeable)Activator.CreateInstance(clrType)!;
                byte[] json = WebApiBodyCodec.EncodeBody(instance, context, options);
                using JsonDocument encoded = JsonDocument.Parse(json);
                foreach (string property in CollectProperties(schemas, schema.Value))
                {
                    if (!encoded.RootElement.TryGetProperty(property, out _))
                    {
                        mismatches.Add($"{schema.Name}.{property}");
                    }
                }
            }

            Assert.That(compared, Is.GreaterThan(100), "too few structures were compared");
            Assert.That(mismatches, Is.Empty);
        }

        /// <summary>
        /// Part 6 Table 42 names the DataValue status field "Status"; the
        /// OpenAPI document names it "StatusCode". The encoder follows
        /// Part 6 and the decoder accepts both. This pins that single
        /// known difference so a correction on either side is noticed.
        /// Reported upstream as
        /// https://github.com/OPCFoundation/UA-Nodeset/issues/146.
        /// </summary>
        [Test]
        public void DataValueDiffersFromDocumentOnlyInStatusFieldName(
            [Values] WebApiEncoding encoding)
        {
            using JsonDocument document = Parse(WebApiServiceSet.AllServices);
            HashSet<string> schema = CollectProperties(
                Schemas(document),
                Schemas(document).GetProperty("DataValue"));
            ServiceMessageContext context = ServiceMessageContext.CreateEmpty(new TestTelemetryContext());
            var value = new DataValue(
                Variant.From(s_sampleArray),
                StatusCodes.BadNodeIdUnknown,
                DateTimeUtc.Now,
                DateTimeUtc.Now);

            using var buffer = new System.IO.MemoryStream();
            using (var encoder = new JsonEncoder(buffer, context, WebApiMediaType.ToEncoderOptions(encoding)))
            {
                encoder.WriteDataValue("Dv", value);
            }
            using JsonDocument encoded = JsonDocument.Parse(buffer.ToArray());
            var emitted = encoded.RootElement.GetProperty("Dv")
                .EnumerateObject()
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);

            Assert.That(emitted.Except(schema), Is.EquivalentTo(s_part6StatusField));
            Assert.That(schema, Does.Contain("StatusCode"));
            Assert.That(emitted, Does.Not.Contain("StatusCode"));
        }

        private static JsonDocument Parse(WebApiServiceSet serviceSet)
        {
            return JsonDocument.Parse(WebApiOpenApiDocument.GetNormativeDocument(serviceSet).Memory);
        }

        private static JsonElement Schemas(JsonDocument document)
        {
            return document.RootElement.GetProperty("components").GetProperty("schemas");
        }

        private static void AssertPathsMatch(
            JsonDocument document,
            IReadOnlyList<WebApiServiceRoute> routes)
        {
            JsonElement paths = document.RootElement.GetProperty("paths");
            Assert.That(
                paths.EnumerateObject().Select(p => p.Name),
                Is.EquivalentTo(routes.Select(r => r.Path)));

            foreach (WebApiServiceRoute route in routes)
            {
                JsonElement post = paths.GetProperty(route.Path).GetProperty("post");
                Assert.That(post.GetProperty("operationId").GetString(), Is.EqualTo(route.OperationId), route.Path);
                Assert.That(
                    SchemaName(post.GetProperty("requestBody")),
                    Is.EqualTo(route.RequestType.Name),
                    route.Path);
                Assert.That(
                    SchemaName(post.GetProperty("responses").GetProperty("200")),
                    Is.EqualTo(route.ResponseType.Name),
                    route.Path);
            }
        }

        private static string SchemaName(JsonElement body)
        {
            string reference = body
                .GetProperty("content")
                .GetProperty(WebApiMediaType.ContentType)
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString()!;
            return reference[(reference.LastIndexOf('/') + 1)..];
        }

        /// <summary>
        /// The properties of a schema including those inherited through
        /// <c>allOf</c>.
        /// </summary>
        private static HashSet<string> CollectProperties(JsonElement schemas, JsonElement schema)
        {
            var properties = new HashSet<string>(StringComparer.Ordinal);
            if (schema.TryGetProperty("allOf", out JsonElement allOf))
            {
                foreach (JsonElement parent in allOf.EnumerateArray())
                {
                    string reference = parent.GetProperty("$ref").GetString()!;
                    JsonElement parentSchema = schemas.GetProperty(reference[(reference.LastIndexOf('/') + 1)..]);
                    properties.UnionWith(CollectProperties(schemas, parentSchema));
                }
            }
            if (schema.TryGetProperty("properties", out JsonElement own))
            {
                foreach (JsonProperty property in own.EnumerateObject())
                {
                    properties.Add(property.Name);
                }
            }
            return properties;
        }

        private static string StripBits(string name)
        {
            return name.EndsWith("Bits", StringComparison.Ordinal) ? name[..^4] : name;
        }

        private static Type? FindClrType(string name)
        {
            foreach (Type anchor in s_anchorTypes)
            {
                Type? type = anchor.Assembly.GetType("Opc.Ua." + name, throwOnError: false);
                if (type != null)
                {
                    return type;
                }
            }
            return null;
        }

        private sealed class TestTelemetryContext : TelemetryContextBase
        {
            public TestTelemetryContext()
                : base(NullLoggerFactory.Instance)
            {
            }
        }
    }
}
