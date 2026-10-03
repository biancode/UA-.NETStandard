# Vendored OPC UA OpenAPI documents

The two documents in this folder are the normative OpenAPI mapping of the
OPC UA services (Part 6 §G.3, `info.version` 1.5.7), copied unchanged from
[`UA-Nodeset/OpenApi`](https://github.com/OPCFoundation/UA-Nodeset/tree/latest/OpenApi)
at commit `4b79bcfaaa44929d8b50158d25b2f57d86aed5e8` (2026-08-18).

| File | Services |
| --- | --- |
| `opc.ua.openapi.allservices.json` | all 28 services mapped by `WebApiServiceRoutes.Routes` |
| `opc.ua.openapi.sessionless.json` | the 8 services in `WebApiServiceRoutes.SessionlessRoutes` |

They are embedded in the assembly and served by
`WebApiOpenApiDocument` with only the `servers` list rewritten. Do not edit
them by hand. To update, copy the new publication over these files, bump
`WebApiOpenApiDocument.SpecificationVersion` if `info.version` changed, and
run `WebApiOpenApiConformanceTests`: they compare the route table, the
enumerations and the encoder output against the documents, so any drift in
the new publication shows up there.

The generated .NET client stub,
[`opcua-webapi-dotnet`](https://github.com/OPCFoundation/opcua-webapi-dotnet),
is built from the same documents; `WebApiStubInteropTests` sends request
bodies shaped the way that stub serializes them.
