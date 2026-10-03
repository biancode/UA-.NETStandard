# OPC UA over OpenAPI

This guide shows how to put an OPC UA server built with this stack behind
the OPC Foundation's OpenAPI documents, and how to talk to it from generated
REST clients. It is task-oriented: expose the documents, generate a client,
get the JSON right, and avoid the traps the published documents contain.

The binding itself (routes, encoding negotiation, discovery, WebSocket
sub-profile) is described in the [REST binding reference](WebApi.md).

## Contents

- [When to use the REST binding](#when-to-use-the-rest-binding)
- [The two documents](#the-two-documents)
- [Serving the document](#serving-the-document)
- [Generating a client](#generating-a-client)
- [Calling the server](#calling-the-server)
- [.NET clients without code generation](#net-clients-without-code-generation)
- [Wire format cheat sheet](#wire-format-cheat-sheet)
- [Known issues with generated clients](#known-issues-with-generated-clients)
- [Updating the documents](#updating-the-documents)
- [Troubleshooting](#troubleshooting)

## When to use the REST binding

| You have | Use |
| --- | --- |
| A .NET application that can reference this stack | `ManagedSession` over `opc.tcp`, or over REST with `UseWebApiEndpoint` if only HTTPS gets through the network |
| A web front end, a script, a low-code platform, a language without an OPC UA SDK | The REST binding with a client generated from the OpenAPI document |
| Subscriptions, events, browsing large address spaces | A session. Over REST this means long-polled `/publish`; `opc.tcp` is cheaper |

The REST binding runs on the same Kestrel listener as the HTTPS binary and
`opcua+uajson` transports: one port, no extra process.

## The two documents

The OPC Foundation publishes the mapping of the OPC UA services to
OpenAPI 3.0 in
[`UA-Nodeset/OpenApi`](https://github.com/OPCFoundation/UA-Nodeset/tree/latest/OpenApi).
Both documents ship inside `Opc.Ua.Bindings.Https`, unchanged and pinned to
one upstream commit (see
[`WebApi/OpenApi/README.md`](../src/Opc.Ua.Bindings.Https/WebApi/OpenApi/README.md)).

| Document | `WebApiServiceSet` | Services |
| --- | --- | --- |
| `opc.ua.openapi.allservices.json` | `AllServices` | All 28: Discovery, Session, View, Attribute, Method, MonitoredItem, Subscription |
| `opc.ua.openapi.sessionless.json` | `Sessionless` | Read, Write, HistoryRead, HistoryUpdate, Call, Browse, BrowseNext, TranslateBrowsePathsToNodeIds |

Every service is a `POST` to a lower-case path (`/read`, `/browse`,
`/createsubscription`, …). The body is the bare `<Service>Request` and
the answer is the bare `<Service>Response`, with no envelope. NodeManagement
and Query are not part of either document.

## Serving the document

`AddWebApiTransport()` mounts the REST routes and serves the matching
document at `GET /openapi.json`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Opc.Ua.Bindings.WebApi;

services.AddOpcUa()
    .AddHttpsTransport()
    .AddWebApiTransport(opt =>
    {
        opt.ServiceSet = WebApiServiceSet.AllServices;  // default
        opt.OpenApiDocumentPath = "/openapi.json";      // default; null disables the route
        opt.OpenApiServerUrl = null;                    // default: relative, see below
    });
```

The server needs an `https://` base address; the
[Transports guide](Transports.md) shows how to configure one.

What the route serves:

- **The published document, byte for byte, except `servers`.** The upstream
  files point at `http://localhost:4840`. The binding replaces that with the
  request's path base as a relative URL (`/`, or `/opcua/` behind
  `UsePathBase("/opcua")`), which OpenAPI 3.0 clients resolve against the
  address they loaded the document from.
- **Anonymous access,** like `/findservers` and `/getendpoints`. The
  document is the published specification and reveals nothing about the
  address space.

Behind a reverse proxy that rewrites host or path, advertise the external
address explicitly:

```csharp
opt.OpenApiServerUrl = "https://plant.example.com/opcua/";
```

### Sessionless only

```csharp
.AddWebApiTransport(opt => opt.ServiceSet = WebApiServiceSet.Sessionless)
```

Only the eight sessionless routes are mapped, and `/openapi.json` serves the
sessionless document, so a generated client contains exactly the operations
the binding maps. Discovery and session management remain available on the
binary and `opcua+uajson` endpoints of the same listener, for clients that
need them.

The service set only limits the routes. Whether the server answers a
request that carries no session (OPC 10000-4 §6.3) is decided by the
server's session manager: without a `ValidateSessionLessRequest` handler on
`ISessionManager`, such a request is answered with `Bad_SessionIdInvalid`.

### Composing your own pipeline

Hosts that build their own ASP.NET Core pipeline instead of using
`AddWebApiTransport` map the same endpoints directly:

```csharp
app.UseRouting();
app.UseEndpoints(endpoints =>
{
    endpoints.MapWebApiEndpoints(WebApiServiceSet.Sessionless);
    endpoints.MapWebApiOpenApiDocument(
        WebApiServiceSet.Sessionless,
        pattern: "/spec/opcua.json",
        serverUrl: "https://plant.example.com/");
});
```

`WebApiOpenApiDocument.GetNormativeDocument(set)` returns the embedded bytes
unchanged, e.g. to publish them to an API portal at build time.

## Generating a client

Point any OpenAPI 3.0 generator at the running server:

```bash
# .NET
openapi-generator-cli generate -g csharp \
    -i https://server:4843/openapi.json -o ./opcua-client \
    --additional-properties=packageName=Plant.OpcUa.Rest,targetFramework=net8.0

# TypeScript (fetch)
openapi-generator-cli generate -g typescript-fetch \
    -i https://server:4843/openapi.json -o ./opcua-client-ts

# Python
openapi-generator-cli generate -g python \
    -i https://server:4843/openapi.json -o ./opcua-client-py
```

The OPC Foundation publishes ready-generated clients built from the same
documents, so you can skip generation entirely:

| Language | Repository |
| --- | --- |
| .NET | [opcua-webapi-dotnet](https://github.com/OPCFoundation/opcua-webapi-dotnet) |
| Python | [opcua-webapi-python](https://github.com/OPCFoundation/opcua-webapi-python) |
| TypeScript | [opcua-webapi-typescript](https://github.com/OPCFoundation/opcua-webapi-typescript) |

A client generated from the sessionless document has eight operations; one
generated from `allservices` has 28. Generate against the document your
server actually serves.

## Calling the server

### The generated .NET client (opcua-webapi-dotnet)

```csharp
using Opc.Ua.WebApi.Api;
using Opc.Ua.WebApi.Client;
using Opc.Ua.WebApi.Model;

var api = new DefaultApi(new Configuration { BasePath = "https://server:4843" });

ReadResponse response = await api.ReadAsync(new ReadRequest
{
    RequestHeader = new RequestHeader
    {
        AuthenticationToken = sessionToken, // from /createsession, see below
        TimeoutHint = 10000
    },
    TimestampsToReturn = 2, // Both
    NodesToRead =
    [
        new ReadValueId { NodeId = "ns=2;s=Pump01.Measurements.Flow", AttributeId = 13 }
    ]
});

DataValue flow = response.Results[0];
Console.WriteLine($"{flow.Value} (UaType {flow.UaType})");
```

Set `BasePath` explicitly: the stub's default is `http://localhost:4840`,
taken from the upstream document. Before relying on `flow.StatusCode`, read
[the DataValue status issue](#datavalue-status-is-always-good) below.

### Sessions over REST

The session services work like their binary counterparts; the session is
identified by the `AuthenticationToken` in every `RequestHeader`:

1. `POST /createsession` → keep `AuthenticationToken` from the response.
2. `POST /activatesession` with that token and a `UserIdentityToken`.
3. Every further request carries
   `"RequestHeader": { "AuthenticationToken": "<token>" }`.
4. `POST /closesession` when done.

REST requests run against the REST endpoint the server announces for its
`https://` base address (`TransportProfileUri`
`http://opcfoundation.org/UA-Profile/Transport/https-uajson-openapi`,
OPC 10000-6 §7.4.1); `/getendpoints` lists it with its user token policies.

For subscriptions, call `/createsubscription` and `/createmonitoreditems`,
then keep one `/publish` request outstanding. The server holds each
`/publish` request open until notifications arrive or the request's
`TimeoutHint` expires. Your HTTP client's timeout must be longer than that
hint (see [Long-poll `/publish`](WebApi.md#long-poll-publish)).

## .NET clients without code generation

If the client can reference this stack, you do not need a generated client:

| API | When |
| --- | --- |
| `ManagedSessionBuilder.UseWebApiEndpoint(url)` | You want the full `ISession`: reconnect, subscriptions, the typed companion-spec clients, all over REST |
| `WebApiClient` (`IWebApiClient`) | You want plain request/response calls with the stack's own request types and no session management |

```csharp
using Opc.Ua;
using Opc.Ua.Client.WebApi;

using var http = new HttpClient { BaseAddress = new Uri("https://server:4843/") };
using var client = new WebApiClient(http, new WebApiClientOptions
{
    Encoding = WebApiEncoding.Compact
});

ReadResponse response = await client.ReadAsync(new ReadRequest
{
    RequestHeader = new RequestHeader { AuthenticationToken = sessionToken },
    NodesToRead = new ReadValueId[]
    {
        new() { NodeId = VariableIds.Server_ServerStatus_CurrentTime, AttributeId = Attributes.Value }
    }
});
```

Both use the stack's own encoder and are therefore not affected by the
[known issues](#known-issues-with-generated-clients) below.

## Wire format cheat sheet

Useful when writing requests by hand or debugging a generated client. The
full rules are in [Part 6 §5.4](https://reference.opcfoundation.org/Core/Part6/v105/docs/5.4).

| OPC UA type | JSON | Example |
| --- | --- | --- |
| NodeId | String | `"i=2258"`, `"ns=2;s=Pump01"`, `"nsu=http://opcfoundation.org/UA/;i=2258"` |
| QualifiedName | String | `"2:Flow"` |
| Enumeration | Integer | `"TimestampsToReturn": 2` |
| DateTime | ISO 8601 string | `"2026-09-23T10:15:30.1234567+02:00"` |
| StatusCode | Object, omitted when Good | `{ "Code": 2150891520 }` (Verbose adds `"Symbol"`) |
| LocalizedText | Object | `{ "Locale": "en", "Text": "Pump" }` |
| Variant | `UaType` + `Value` (+ `Dimensions` for matrices) | `{ "UaType": 6, "Value": 42 }`, `{ "UaType": 11, "Value": [1.5, 2.5] }` |
| DataValue | The Variant's fields plus status and timestamps | `{ "UaType": 6, "Value": 42, "SourceTimestamp": "…" }` |
| ExtensionObject | `UaTypeId` + body | see below |

Common `UaType` values: 1 Boolean, 6 Int32, 7 UInt32, 10 Float, 11 Double,
12 String, 13 DateTime, 17 NodeId, 21 LocalizedText, 22 ExtensionObject.

**Omitted fields decode as their default.** A request only needs the fields
it sets; `"MaxAge"`, `"RequestHandle"` and friends can be left out.

**Encoding flavour.** The server answers in Compact JSON unless the request
asks for Verbose (`Content-Type` or `Accept: application/json;
encoding=verbose`). Verbose keeps default values and adds status symbols,
which helps when debugging.

**ExtensionObject bodies** come in two forms. A client built with this
stack sends the structure's fields inline next to `UaTypeId`. Generated
clients cannot, because the document's `ExtensionObject` schema only has
`UaTypeId`, `UaEncoding` and `UaBody`, so they send the OPC UA *binary*
encoding, base64 in `UaBody`, with `UaEncoding: 1`. The server accepts both.
An anonymous identity token for `/activatesession`, as a generated client
sends it:

```json
"UserIdentityToken": {
  "UaTypeId": "i=321",
  "UaEncoding": 1,
  "UaBody": "CQAAAGFub255bW91cw=="
}
```

`i=321` is `AnonymousIdentityToken_Encoding_DefaultBinary`; the body is the
binary-encoded `PolicyId` `"anonymous"` (Int32 length, then UTF-8).

## Known issues with generated clients

### DataValue status is always Good

Part 6 [Table 42](https://reference.opcfoundation.org/Core/Part6/v105/docs/5.4.2.18)
names the DataValue status field `"Status"`. All published JSON and OpenAPI
schemas, and therefore every generated client, name it `"StatusCode"`. The
discrepancy is reported as
[UA-Nodeset#146](https://github.com/OPCFoundation/UA-Nodeset/issues/146).

| Direction | Effect |
| --- | --- |
| Server → generated client (Read, HistoryRead, Publish results) | The server writes `"Status"` per Part 6; the client ignores it and reports **Good** for bad and uncertain values |
| Generated client → server (e.g. Write) | The server accepts both names, so the status arrives |

Until upstream resolves it, a generated client that must see value quality
has two options:

- Read the raw JSON and take `Status` yourself. For the .NET stub,
  `ApiResponse<ReadResponse>.RawContent` from `ReadWithHttpInfoAsync` holds
  the body.
- Add `Status` to the generated `DataValue` model, e.g. in a partial class
  for the .NET stub, or by patching the document before generating.

The stack's own clients (`ManagedSession`, `WebApiClient`) are unaffected.

### Validating responses against the schemas

The `DataValue` schema sets `"additionalProperties": false`. A validator that
enforces it rejects every DataValue with a non-Good status, for the same
reason. Disable response validation or patch the schema until
UA-Nodeset#146 is resolved.

### Polymorphic structures

Filters (`DataChangeFilter`, `EventFilter`), notifications and identity
tokens are ExtensionObjects. Generated clients only see the
`UaTypeId`/`UaEncoding`/`UaBody` envelope, so they have to produce and
consume the binary encoding (see the
[cheat sheet](#wire-format-cheat-sheet)). If your client needs event
filters or subscriptions, a .NET client using
[`UseWebApiEndpoint`](#net-clients-without-code-generation) is considerably
less work.

## Updating the documents

The embedded documents are pinned. To move to a newer publication:

1. Copy the new `opc.ua.openapi.allservices.json` and
   `opc.ua.openapi.sessionless.json` over
   `src/Opc.Ua.Bindings.Https/WebApi/OpenApi/`.
2. Update the commit reference in the README next to them, and
   `WebApiOpenApiDocument.SpecificationVersion` if `info.version` changed.
3. Run the conformance tests:

   ```bash
   dotnet test tests/Opc.Ua.Bindings.Https.WebApi.Tests \
       --filter "FullyQualifiedName~.OpenApi."
   ```

The tests compare the new documents with the route table, the CLR
enumerations and what the encoder emits for more than 100 structures. A
failure names the path, enumeration member or property that drifted.
`DataValueDiffersFromDocumentOnlyInStatusFieldName` fails on purpose once
UA-Nodeset#146 is fixed on either side; then drop the decoder alias or
switch the encoder, whichever way the fix went.

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `GET /openapi.json` returns 404 | `OpenApiDocumentPath` is null or changed, or the REST binding isn't registered | Check `AddWebApiTransport` and the configured path |
| Generated client calls `http://localhost:4840` | Client built from the upstream file, not from the server | Set the client's base path, or regenerate from the server's `/openapi.json` |
| Swagger UI "Try it out" goes to the wrong host behind a proxy | Relative server URL resolved against the proxy's internal address | Set `OpenApiServerUrl` to the external address |
| `/createsession` returns an HTTP error, `/read` works | `ServiceSet = Sessionless`: the route is not mapped and the request falls through to the listener's binary/JSON handler | Use `AllServices`, or the binary endpoint for sessions |
| HTTP 400, empty body | The body is not valid OPC UA JSON for that request (misspelled property, wrong type, NodeId not parseable) | Compare with the [cheat sheet](#wire-format-cheat-sheet); the server logs the decode error at Information level |
| `ServiceResult` `BadSessionIdInvalid` | Request without, or with a stale, `AuthenticationToken` | Create and activate a session first; send its token in every `RequestHeader` |
| Bad values show up as Good in a generated client | [UA-Nodeset#146](#datavalue-status-is-always-good) | Read `Status` from the raw response |
| `/publish` times out on the client (`BadTimeout`) | HTTP client timeout shorter than `TimeoutHint` | Raise the client timeout above the hint |
