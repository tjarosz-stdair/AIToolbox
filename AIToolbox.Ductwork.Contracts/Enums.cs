using System.Text.Json.Serialization;

namespace AIToolbox.Ductwork.Contracts;

// All enums serialize as strings so the JSON Schema and the stored data stay readable and stable
// when members are added.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProducerKind { Llm, Algorithm, Manual }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DetectionMethod { Geometry, Text, FlagNote, Symbol, Schedule, Vision, Manual }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QuantityUnit { EA, LF, SF }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewStatus { Proposed, Verified, Rejected, Manual }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Disposition { Included, Excluded, FactoryFurnished, TBD }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResolutionSource { Detector, Rule, Override, UserAdded }

/// <summary>How much the tool interrupts the user (plan section 6.13).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewLevel { Sensitive, Normal, Insensitive }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SurfacedAs { Finding, Batch }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FindingKind { AmbiguousLabel, GrammarVariant, ReferenceDisagreement, SymbolOnly, MissingEvidence, LowConfidence, Other }

/// <summary>Detector families A-E from plan section 6.7.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DetectorFamily { ScheduleNote, GeometryNetwork, LabelFlagCount, SymbolMatch, OcrVision }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActivityState { Active, Idle }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowStep { Setup, Process, Review, Report }
