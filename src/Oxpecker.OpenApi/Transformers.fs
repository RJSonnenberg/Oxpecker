namespace Oxpecker.OpenApi

open System
open System.Collections.Generic
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.OpenApi
open Microsoft.FSharp.Reflection
open Microsoft.OpenApi
open FSharp.Control
open type Microsoft.AspNetCore.Http.TypedResults

module private Helpers =
    let nullSchema = OpenApiSchema(Type = Nullable(JsonSchemaType.Null))

    let (|FSharpOptionKind|_|) (t: Type) =
        if t.IsGenericType then
            let gtd = t.GetGenericTypeDefinition()
            if gtd = typedefof<option<_>> || gtd = typedefof<ValueOption<_>> then
                Some(t.GetGenericArguments()[0])
            else
                None
        else
            None

    let (|OptionalProperties|_|) (ctx: OpenApiSchemaTransformerContext) =
        let result = ResizeArray()
        for propertyInfo in ctx.JsonTypeInfo.Properties do
            match propertyInfo.PropertyType with
            | FSharpOptionKind innerType -> result.Add((propertyInfo, innerType))
            | _ -> ()
        if result.Count > 0 then Some(result) else None

    /// Union an existing JsonSchemaType with `null` (OpenAPI 3.1), and also drives `nullable: true` for 3.0.
    let unionWithNull (t: Nullable<JsonSchemaType>) : Nullable<JsonSchemaType> =
        if t.HasValue then
            let combined =
                LanguagePrimitives.EnumOfValue((int t.Value) ||| (int JsonSchemaType.Null))
            Nullable<JsonSchemaType>(combined)
        else
            // Leave as null; writer will omit 'type'.
            Nullable()

    let tryGetRefSchema (schema: OpenApiSchema) =
        match schema.Metadata with
        | null -> None
        | metadata ->
            match metadata.TryGetValue("x-schema-id") with
            | true, o ->
                match o with
                | :? string as s ->
                    match String.IsNullOrEmpty s with
                    | true -> None
                    | false -> Some s
                | _ -> None
            | _ -> None

    let copyMetadata (dst: OpenApiSchema) refSchemaId isSimple (src: IOpenApiSchema) =
        if src :? IMetadataContainer then
            match (src :?> IMetadataContainer).Metadata with
            | null -> ()
            | srcMeta ->
                let dstMeta =
                    match dst.Metadata with
                    | null ->
                        let dict = Dictionary<string, obj>() :> IDictionary<string, obj>
                        dst.Metadata <- dict
                        dict
                    | m -> m
                for KeyValue(k, v) in srcMeta do
                    match k with
                    | "x-schema-id" -> dstMeta[k] <- refSchemaId
                    | "x-ref-description" ->
                        if isSimple then
                            dst.Description <- v :?> string
                        else
                            dstMeta[k] <- v
                    | _ -> ()

type FSharpOptionSchemaTransformer() =
    interface IOpenApiSchemaTransformer with
        member _.TransformAsync
            (schema: OpenApiSchema, ctx: OpenApiSchemaTransformerContext, ct: CancellationToken)
            : Task =
            task {
                match ctx with
                | Helpers.OptionalProperties props ->
                    for propInfo, innerType in props do
                        let key = propInfo.Name
                        match schema.Properties with
                        | null -> ()
                        | props when not(props.ContainsKey key) -> ()
                        | props ->
                            let propSchema = props[key]
                            let! innerSchema = ctx.GetOrCreateSchemaAsync(innerType, null, ct)
                            // If it's a reference (complex type), use oneOf [null, $ref].
                            // If it's inline (simple type), just add null to the type.
                            let newSchema =
                                match Helpers.tryGetRefSchema innerSchema with
                                | None ->
                                    propSchema |> Helpers.copyMetadata innerSchema "" true
                                    innerSchema.Type <- Helpers.unionWithNull innerSchema.Type
                                    innerSchema
                                | Some refSchema ->
                                    propSchema |> Helpers.copyMetadata innerSchema refSchema false
                                    let items = ResizeArray<IOpenApiSchema>()
                                    items.Add(Helpers.nullSchema)
                                    items.Add(innerSchema)
                                    OpenApiSchema(OneOf = items)
                            props[key] <- newSchema
                | _ -> ()
            }
            :> Task

module private UnionHelpers =
    /// Create schema for primitive field types; returns None for complex types that should be resolved via OpenAPI.
    let tryCreatePrimitiveFieldSchema (fieldType: Type) =
        let schema = OpenApiSchema()
        let setType schemaType format =
            schema.Type <- Nullable(schemaType)
            schema.Format <- format
            Some schema
        match fieldType with
        // Integer types
        | t when t = typeof<int> || t = typeof<int32> -> setType JsonSchemaType.Integer "int32"
        | t when t = typeof<int64> -> setType JsonSchemaType.Integer "int64"
        | t when t = typeof<int16> -> setType JsonSchemaType.Integer "int16"
        | t when t = typeof<sbyte> || t = typeof<int8> -> setType JsonSchemaType.Integer "int8"
        | t when t = typeof<byte> || t = typeof<uint8> -> setType JsonSchemaType.Integer "uint8"
        | t when t = typeof<uint16> -> setType JsonSchemaType.Integer "uint16"
        | t when t = typeof<uint32> || t = typeof<uint> -> setType JsonSchemaType.Integer "uint32"
        | t when t = typeof<uint64> -> setType JsonSchemaType.Integer "uint64"
        | t when t = typeof<nativeint> || t = typeof<IntPtr> -> setType JsonSchemaType.Integer "int64"
        | t when t = typeof<unativeint> || t = typeof<UIntPtr> -> setType JsonSchemaType.Integer "uint64"
        | t when t = typeof<bigint> || t = typeof<System.Numerics.BigInteger> ->
            schema.Type <- Nullable(JsonSchemaType.Integer)
            Some schema
        // Floating point types
        | t when t = typeof<float> || t = typeof<double> -> setType JsonSchemaType.Number "double"
        | t when t = typeof<float32> || t = typeof<single> -> setType JsonSchemaType.Number "float"
        | t when t = typeof<decimal> -> setType JsonSchemaType.Number "decimal"
        // Boolean
        | t when t = typeof<bool> ->
            schema.Type <- Nullable(JsonSchemaType.Boolean)
            Some schema
        // String and character types
        | t when t = typeof<string> ->
            schema.Type <- Nullable(JsonSchemaType.String)
            Some schema
        | t when t = typeof<char> ->
            schema.Type <- Nullable(JsonSchemaType.String)
            schema.MinLength <- Nullable(1)
            schema.MaxLength <- Nullable(1)
            Some schema
        // Date and time types
        | t when t = typeof<DateTime> -> setType JsonSchemaType.String "date-time"
        | t when t = typeof<DateTimeOffset> -> setType JsonSchemaType.String "date-time"
        | t when t = typeof<TimeSpan> -> setType JsonSchemaType.String "duration"
        // Guid
        | t when t = typeof<Guid> -> setType JsonSchemaType.String "uuid"
        // Unit type
        | t when t = typeof<unit> ->
            schema.Type <- Nullable(JsonSchemaType.Null)
            Some schema
        // Complex types should be resolved via OpenAPI schema creation.
        | _ -> None
    /// Check if this is an F# option type (handled separately by FSharpOptionSchemaTransformer)
    let isOptionType (t: Type) =
        if t.IsGenericType then
            let gtd = t.GetGenericTypeDefinition()
            gtd = typedefof<option<_>> || gtd = typedefof<ValueOption<_>>
        else
            false

    /// Create a schema for a field type
    let createFieldSchema (fieldType: Type) =
        let schema = OpenApiSchema()
        match fieldType with
        // Integer types
        | t when t = typeof<int> || t = typeof<int32> ->
            schema.Type <- JsonSchemaType.Integer
            schema.Format <- "int32"
        | t when t = typeof<int64> ->
            schema.Type <- JsonSchemaType.Integer
            schema.Format <- "int64"
        | t when t = typeof<int16> ->
            schema.Type <- JsonSchemaType.Integer
            schema.Format <- "int16"
        | t when t = typeof<sbyte> || t = typeof<int8> ->
            schema.Type <- JsonSchemaType.Integer
            schema.Format <- "int8"
        | t when t = typeof<byte> || t = typeof<uint8> ->
            schema.Type <- JsonSchemaType.Integer
            schema.Format <- "uint8"
        | t when t = typeof<uint16> ->
            schema.Type <- JsonSchemaType.Integer
            schema.Format <- "uint16"
        | t when t = typeof<uint32> || t = typeof<uint> ->
            schema.Type <- JsonSchemaType.Integer
            schema.Format <- "uint32"
        | t when t = typeof<uint64> ->
            schema.Type <- JsonSchemaType.Integer
            schema.Format <- "uint64"
        | t when t = typeof<nativeint> || t = typeof<IntPtr> ->
            schema.Type <- JsonSchemaType.Integer
            schema.Format <- "int64"
        | t when t = typeof<unativeint> || t = typeof<UIntPtr> ->
            schema.Type <- JsonSchemaType.Integer
            schema.Format <- "uint64"
        | t when t = typeof<bigint> || t = typeof<System.Numerics.BigInteger> ->
            schema.Type <- JsonSchemaType.Integer
        // Floating point types
        | t when t = typeof<float> || t = typeof<double> ->
            schema.Type <- JsonSchemaType.Number
            schema.Format <- "double"
        | t when t = typeof<float32> || t = typeof<single> ->
            schema.Type <- JsonSchemaType.Number
            schema.Format <- "float"
        | t when t = typeof<decimal> ->
            schema.Type <- JsonSchemaType.Number
            schema.Format <- "decimal"
        // Boolean
        | t when t = typeof<bool> ->
            schema.Type <- JsonSchemaType.Boolean
        // String and character types
        | t when t = typeof<string> ->
            schema.Type <- JsonSchemaType.String
        | t when t = typeof<char> ->
            schema.Type <- JsonSchemaType.String
            schema.MinLength <- Nullable(1)
            schema.MaxLength <- Nullable(1)
        // Date and time types
        | t when t = typeof<DateTime> ->
            schema.Type <- JsonSchemaType.String
            schema.Format <- "date-time"
        | t when t = typeof<DateTimeOffset> ->
            schema.Type <- JsonSchemaType.String
            schema.Format <- "date-time"
        | t when t = typeof<TimeSpan> ->
            schema.Type <- JsonSchemaType.String
            schema.Format <- "duration"
        // Guid
        | t when t = typeof<Guid> ->
            schema.Type <- JsonSchemaType.String
            schema.Format <- "uuid"
        // Unit type
        | t when t = typeof<unit> ->
            schema.Type <- JsonSchemaType.Null
        // Complex types
        | _ ->
            schema.Type <- JsonSchemaType.Object
        schema

    /// Create enum values from union case names
    let createEnumValues (cases: UnionCaseInfo[]) =
        cases
        |> Array.choose (fun c -> c.Name |> JsonValue.Create |> Option.ofObj |> Option.map (fun v -> v :> JsonNode))
        |> ResizeArray

    /// Generate schema for simple union (enum-like, no fields)
    let generateSimpleUnionSchema (schema: OpenApiSchema) (cases: UnionCaseInfo[]) =
        schema.Type <- JsonSchemaType.String
        schema.Enum <- createEnumValues cases
        if String.IsNullOrEmpty(schema.Description) then
            let caseNames = cases |> Array.map (fun c -> c.Name) |> String.concat ", "
            schema.Description <- sprintf "F# union type with values: %s" caseNames
        // Clear properties that shouldn't be set for simple unions
        schema.Properties <- Unchecked.defaultof<_>
        schema.Required <- Unchecked.defaultof<_>

    /// Generate schema for a single union case with fields (discriminator pattern)
    /// Format: { "type": "CaseName", "field1": value1, "field2": value2, ... }
    let generateCaseSchemaAsync
        (case: UnionCaseInfo)
        (ctx: OpenApiSchemaTransformerContext)
        (ct: CancellationToken)
        =
        task {
            let fields = case.GetFields()
            let caseSchema = OpenApiSchema()
            caseSchema.Type <- JsonSchemaType.Object
            let required = HashSet<string>(["type"])  // "type" discriminator is always required
            let properties = Dictionary<string, IOpenApiSchema>()
            // Add discriminator property
            let discriminatorSchema = OpenApiSchema()
            discriminatorSchema.Type <- JsonSchemaType.String
            match JsonValue.Create(case.Name) with
            | null -> ()
            | value -> discriminatorSchema.Enum <- ResizeArray([value :> JsonNode])
            properties.["type"] <- discriminatorSchema
            // Add named field properties
            for field in fields do
                let fieldName = field.Name.ToLowerInvariant()  // Use camelCase
                required.Add(fieldName) |> ignore
                match tryCreatePrimitiveFieldSchema field.PropertyType with
                | Some schema -> properties.[fieldName] <- schema
                | None ->
                    let! fieldSchema = ctx.GetOrCreateSchemaAsync(field.PropertyType, null, ct)
                    properties.[fieldName] <- fieldSchema
            caseSchema.Properties <- properties
            caseSchema.Required <- required
            caseSchema.Description <-
                match fields.Length with
                | 0 -> sprintf "Union case: %s" case.Name
                | n -> sprintf "Union case: %s with %d field(s)" case.Name n
            return caseSchema
        }

    /// Generate schema for complex union (with fields)
    /// Uses oneOf pattern with discriminator
    let generateComplexUnionSchemaAsync
        (schema: OpenApiSchema)
        (cases: UnionCaseInfo[])
        (unionType: Type)
        (ctx: OpenApiSchemaTransformerContext)
        (ct: CancellationToken)
        =
        task {
            let document =
                ctx.Document
                |> Option.ofObj
                |> Option.defaultWith (fun () -> invalidOp "OpenApi document was null.")
            let caseSchemas = List<IOpenApiSchema>()
            let components =
                match Option.ofObj document.Components with
                | Some existing -> existing
                | None ->
                    let created = OpenApiComponents()
                    document.Components <- created
                    created
            let schemas =
                match Option.ofObj components.Schemas with
                | Some existing -> existing
                | None ->
                    let created = Dictionary<string, IOpenApiSchema>()
                    components.Schemas <- created
                    created
            let mapping = Dictionary<string, OpenApiSchemaReference>()
            for caseInfo in cases do
                let! caseSchema = generateCaseSchemaAsync caseInfo ctx ct
                let caseSchemaId = sprintf "%s.%s" unionType.Name caseInfo.Name
                if not (schemas.ContainsKey caseSchemaId) then
                    schemas[caseSchemaId] <- caseSchema
                let caseSchemaRef = OpenApiSchemaReference(caseSchemaId, document, null)
                caseSchemas.Add(caseSchema)
                mapping[caseInfo.Name] <- caseSchemaRef
            schema.Type <- Nullable()
            schema.OneOf <- caseSchemas
            // Set discriminator before description to match expected ordering (oneOf, discriminator, description)
            let discriminator = OpenApiDiscriminator()
            discriminator.PropertyName <- "type"
            discriminator.Mapping <- mapping
            schema.Discriminator <- discriminator
            // Set description after discriminator
            if String.IsNullOrEmpty(schema.Description) then
                let caseSummary = cases |> Array.map (fun c -> c.Name) |> String.concat ", "
                schema.Description <- sprintf "F# discriminated union type with cases: %s" caseSummary
            schema.Properties <- Unchecked.defaultof<_>
            schema.Required <- Unchecked.defaultof<_>
        }

    /// Classify union type and generate appropriate schema
    let transformUnionSchemaAsync
        (schema: OpenApiSchema)
        (unionType: Type)
        (ctx: OpenApiSchemaTransformerContext)
        (ct: CancellationToken)
        =
        task {
            let cases = FSharpType.GetUnionCases(unionType, true)
            let isSimple = cases |> Array.forall (fun case -> case.GetFields().Length = 0)
            match isSimple with
            | true -> generateSimpleUnionSchema schema cases
            | false -> do! generateComplexUnionSchemaAsync schema cases unionType ctx ct
        }

/// <summary>
/// Schema transformer that generates proper OpenAPI schemas for F# discriminated union types.
///
/// This transformer generates schemas that align with the FSharp.SystemTextJson serialization format
/// using adjacent tag with named fields:
/// - Simple unions (no fields) → string enum
/// - Complex unions (with fields) → oneOf with discriminator pattern
///
/// Serialization format:
/// - Simple: "Active" (plain string)
/// - Complex: { "type": "Circle", "radius": 5.0 } (discriminator + named fields)
/// </summary>
type FSharpUnionSchemaTransformer() =
    interface IOpenApiSchemaTransformer with
        member _.TransformAsync
            (schema: OpenApiSchema, ctx: OpenApiSchemaTransformerContext, ct: CancellationToken)
            : Task =
            let targetType = ctx.JsonTypeInfo.Type
            // Process only F# union types (excluding option types)
            task {
                match targetType with
                | t when not(UnionHelpers.isOptionType t) && FSharpType.IsUnion(t, true) ->
                    do! UnionHelpers.transformUnionSchemaAsync schema t ctx ct
                | _ -> ()
            }
            :> Task
