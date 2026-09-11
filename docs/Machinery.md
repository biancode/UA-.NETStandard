# OPC UA for Machinery (OPC 40001)

The `Opc.Ua.Machinery*` packages implement the OPC 40001 specification series —
the building blocks every machine shares, plus the four specialised parts that
production equipment adds depending on its use case.

## Contents

- [Library layout](#library-layout)
- [Models and namespaces](#models-and-namespaces)
- [Dependency specifications](#dependency-specifications)
- [Model sources, identifier tables and repairs](#model-sources-identifier-tables-and-repairs)
- [Generator gaps found while adding these models](#generator-gaps-found-while-adding-these-models)

## Library layout

Each part is its own model assembly, so a server takes only what it serves. A
machine tool that reports jobs and results does not have to carry PADIM.

| Package | Specification | Model URI |
| --- | --- | --- |
| `Opc.Ua.Machinery` | OPC 40001-1 Basic Building Blocks | `http://opcfoundation.org/UA/Machinery/` |
| `Opc.Ua.Machinery.ProcessValues` | OPC 40001-2 Process Values | `…/UA/Machinery/ProcessValues/` |
| `Opc.Ua.Machinery.Jobs` | OPC 40001-3 Job Management | `…/UA/Machinery/Jobs/` |
| `Opc.Ua.Machinery.Energy` | OPC 40001-4 Energy Management | `…/UA/Machinery/Energy/` |
| `Opc.Ua.Machinery.Result` | OPC 40001-101 Result Transfer | `…/UA/Machinery/Result/` |

## Models and namespaces

The generated C# namespace of each model is derived from its model URI by
`NodeSetToModelDesign.CreateNamespace`, and every project pins the derived value
explicitly — it is part of the contract each assembly publishes through
`[assembly: ModelDependencyAttribute]` (see [ModelDependencies.md](ModelDependencies.md)).

| Model URI | C# namespace | `Namespaces` member | Model loader |
| --- | --- | --- | --- |
| `…/UA/Machinery/` | `Opc.Ua.Machinery` | `Machinery` | `AddOpcUaMachinery` |
| `…/UA/Machinery/ProcessValues/` | `Opc.Ua.Machinery.ProcessValues` | `MachineryProcessValues` | `AddOpcUaMachineryProcessValues` |
| `…/UA/Machinery/Jobs/` | `Opc.Ua.Machinery.Jobs` | `MachineryJobs` | `AddOpcUaMachineryJobs` |
| `…/UA/Machinery/Energy/` | `Opc.Ua.Machinery.Energy` | `MachineryEnergy` | `AddOpcUaMachineryEnergy` |
| `…/UA/Machinery/Result/` | `Opc.Ua.Machinery.Result` | `MachineryResult` | `AddOpcUaMachineryResult` |
| `…/UA/IA/` | `Opc.Ua.IA` | `IA` | `AddOpcUaIA` |
| `…/UA/ECM/` | `Opc.Ua.ECM` | `ECM` | `AddOpcUaECM` |
| `…/UA/PADIM/` | `Opc.Ua.PADIM` | `PADIM` | `AddOpcUaPADIM` |
| `…/UA/Dictionary/IRDI` | `Opc.Ua.IRDI` | `IRDI` | `AddOpcUaIRDI` |

Note on placement: the `Machines` folder (`FolderType`) that OPC 40001-1 defines
is `Organizes`-referenced from `Objects`, **not** from the DI `DeviceSet`. There
is no `MachineryItems` folder in 1.04.1, and no `MachineryBuildingBlocksType` —
the building blocks are AddIn instances (`Identification`, `Monitoring`,
`Components`, `MachineryEquipment`, `Notifications`).

## Dependency specifications

Adding OPC 40001 pulled in three specifications the repository did not have, and
moved a fourth out of `Opc.Ua.Robotics`:

| Package | Specification | Why |
| --- | --- | --- |
| `Opc.Ua.IA` | OPC 10000-200 Industrial Automation | 40001-1 types `MonitoringType.Status.Stacklight` with IA's `BasicStacklightType`. IA used to be generated inside `Opc.Ua.Robotics`; two assemblies generating the same model URI under the same C# prefix collide at the consumer, so it moved to its own package and Robotics now references it. The generated types keep living in the `Opc.Ua.IA` namespace and `AddOpcUaIA` is unchanged. |
| `Opc.Ua.PADIM` | OPC 30081 PADIM + the IRDI dictionary | 40001-2 derives `ProcessValueType` / `ProcessValueVariableType` from PADIM's `AnalogSignalType` / `AnalogSignalVariableType`. IRDI ships in the same assembly because PADIM references it several hundred times and is unusable without it. |
| `Opc.Ua.ECM` | OPC 34100 Energy Consumption Management | 40001-4's energy carriers are instances of ECM's `EnergyMeasurementValueType`. |
| `Opc.Ua.ISA95` *(existing)* | OPC 10031-4 ISA-95 Job Control V2 | 40001-3's `JobManagementType` composes the Job Control V2 receiver and response provider. |

The resulting reference graph:

```
Opc.Ua.Core
 └── Opc.Ua.Di ──┬── Opc.Ua.IA ──┬── Opc.Ua.Robotics
                 │               ├── Opc.Ua.ECM ─── Opc.Ua.Machinery.Energy
                 │               └── Opc.Ua.Machinery
                 └── Opc.Ua.PADIM (+ IRDI) ─── Opc.Ua.Machinery.ProcessValues
Opc.Ua.Core ─── Opc.Ua.Machinery.Result
Opc.Ua.ISA95 ─── Opc.Ua.Machinery.Jobs
```

## Model sources, identifier tables and repairs

Every NodeSet is vendored unmodified from
[OPCFoundation/UA-Nodeset](https://github.com/OPCFoundation/UA-Nodeset) (branch
`latest`). Two files are renamed on the way in, because upstream's names do not
match the assembly they belong to; renaming is safe because
`NodeSetToModelDesign.IsNodeSet` inspects the document, not the file name.

| Repository path | Upstream path |
| --- | --- |
| `src/Opc.Ua.Machinery/Model/Opc.Ua.Machinery.NodeSet2.xml` | `Machinery/Opc.Ua.Machinery.NodeSet2.xml` |
| `src/Opc.Ua.Machinery.ProcessValues/Model/Opc.Ua.Machinery.ProcessValues.NodeSet2.xml` | `Machinery/ProcessValues/Opc.Ua.Machinery.ProcessValues.NodeSet2.xml` |
| `src/Opc.Ua.Machinery.Jobs/Model/Opc.Ua.Machinery.Jobs.NodeSet2.xml` | `Machinery/Jobs/Opc.Ua.Machinery.Jobs.Nodeset2.xml` *(lower-case `s`)* |
| `src/Opc.Ua.Machinery.Energy/Model/Opc.Ua.Machinery.Energy.NodeSet2.xml` | `Machinery/Energy/Opc.Ua.Machinery.Energy.NodeSet2.xml` |
| `src/Opc.Ua.Machinery.Result/Model/Opc.Ua.Machinery.Result.NodeSet2.xml` | `Machinery/Result/Opc.Ua.Machinery_Result.NodeSet2.xml` *(underscore)* |
| `src/Opc.Ua.IA/Model/Opc.Ua.IA.NodeSet2.xml` | `IA/Opc.Ua.IA.NodeSet2.xml` |
| `src/Opc.Ua.ECM/Model/Opc.Ua.ECM.NodeSet2.xml` | `ECM/Opc.Ua.ECM.NodeSet2.xml` |
| `src/Opc.Ua.PADIM/Model/Opc.Ua.PADIM.NodeSet2.xml` | `PADIM/Opc.Ua.PADIM.NodeSet2.xml` |
| `src/Opc.Ua.PADIM/Model/Opc.Ua.IRDI.NodeSet2.xml` | `PADIM/Opc.Ua.IRDI.NodeSet2.xml` |

### What the identifier table is and is not

Each model carries a `Model/*.NodeIds.csv` wired up as
`ModelSourceGeneratorIdentifierFile`. It is **not** a NodeId source — for a
NodeSet2 input the NodeIds come from the XML
(`ModelDesignValidator.LoadDesignFile` routes NodeSet2 documents through
`NodeSetToModelDesign.Import`; only ModelDesign inputs use the CSV fallback).
It is a **drift guard**: the generator validates every row against the imported
symbols and fails the build with `MODELGEN022`…`MODELGEN029` when the vendored
model and the pinned mapping disagree. That is how a silently dropped type gets
caught instead of quietly disappearing from the generated API.

`Model/*.Upstream.NodeIds.csv` is the OPC Foundation's publication unmodified
and is deliberately kept out of `<AdditionalFiles>`, so the two can be diffed to
review exactly what the derivation changed — the convention `src/Opc.Ua.ISA95`
established.

### Why the published tables need deriving

The published tables carry the UA-ModelCompiler's symbolic names, which differ
from the ones this repository's generator derives. Three differences occur; all
three are handled by
[`tools/nodesets/derive-identifier-table.py`](../tools/nodesets/derive-identifier-table.py),
which reimplements `NodeSetToModelDesign.ImportSymbolicName` / `BuildSymbolicId`:

| Published | Derived | Why |
| --- | --- | --- |
| `Server_Namespaces_http___…_` | `http___…_` | the publication qualifies the namespace-metadata object with its server path |
| `X_ControlChannel` | `X_ControlChannel_Placeholder` | a `<Placeholder>` BrowseName maps to `Name_Placeholder` |
| `DefaultBinary` | `RGBWDataType_Encoding_DefaultBinary` | encoding nodes are qualified with the data type they encode |

Two publications also deviate in shape rather than in naming, and the script
reads both: **ECM** publishes its table as `Opc.Ua.ECM.NodeSet2.csv` rather than
`*.NodeIds.csv`, and **PADIM** publishes a tab-separated table with an
`ID / Browsename / Node Class` header — identifier first, and BrowseNames rather
than symbolic names. **IRDI** has no published table at all and therefore
carries no drift guard.

No numeric id or node class disagreed with its NodeSet in any of the nine
models, so no NodeSet needed a normative repair of the kind `docs/ISA95.md`
documents for OPC-10030.

## Generator gaps found while adding these models

Two defects in the cross-assembly dependency machinery only surfaced once a
model consumed DI through a referenced assembly's payload rather than through
`<AdditionalFiles>`. Both are fixed; both are worth knowing about when adding
the next companion specification.

**A VariableType's data type restriction was not carried.** `DependencyNode`
recorded base type, numeric id, abstractness, data type fields and children, but
never a VariableType's own `DataType` / `ValueRank`. A consumer that typed a
variable with such a VariableType resolved a null `DataTypeNode`, and
`ModelDesignExtensions.GetNodeStateClassName` dereferenced it — a bare
`NullReferenceException` surfaced as `MODELGEN003`. OPC 40001-1's
`EquipmentLife` and `<LifetimeVariable>`, typed by DI's `LifetimeVariableType`,
are the first nodes in this repository to hit it. This is what the reduced
Machinery NodeSet copies in the samples were working around.

The payload now carries the restriction behind a new node flag, so payloads
written before the change stay readable, and `GetNodeStateClassName` throws a
message naming the variable and its type rather than a bare null dereference if
a gap ever remains.

**A method declaration was not chained to its method state.** When a payload
child carried both a method-state identity and a declaration identity, the
consumer replaced the method-state node with the declaration node without
linking the two. `ResolveMethodStateIdentity` then stopped at the declaration —
whose symbolic id is the composed `OwnerType_Method` — and a consumer that
re-declares an inherited method emitted a reference to an
`OwnerType_MethodMethodState` class the producer never generated. OPC 34100 ECM
re-declaring the DI `LockingServices` methods is the first model to hit it.

## See also

- [Device Integration (DI) developer guide](DeviceIntegration.md)
- [ISA-95 developer guide](ISA95.md) — OPC 40001-3 sits on Job Control V2
- [Cross-assembly model dependencies](ModelDependencies.md)
- [Robotics developer guide](Robotics.md) — shares `Opc.Ua.IA`
