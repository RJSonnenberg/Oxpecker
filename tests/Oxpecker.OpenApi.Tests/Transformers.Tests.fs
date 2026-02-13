module Oxpecker.OpenApi.Tests.Transformers

open System
open System.ComponentModel
open System.ComponentModel.DataAnnotations
open System.Net
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Oxpecker.OpenApi
open Xunit
open FsUnit.Light
open Oxpecker

module WebApp =

    let webApp (endpoints: Endpoint seq) =
        task {
            let host =
                HostBuilder()
                    .ConfigureWebHost(fun webHostBuilder ->
                        webHostBuilder
                            .UseTestServer()
                            .Configure(fun app ->
                                app
                                    .UseRouting()
                                    .UseEndpoints(fun builder ->
                                        builder.MapOxpeckerEndpoints(endpoints)
                                        builder.MapOpenApi() |> ignore)
                                |> ignore)
                            .ConfigureServices(fun services ->
                                services
                                    .AddRouting()
                                    .AddOpenApi(fun o ->
                                        o.AddSchemaTransformer<FSharpOptionSchemaTransformer>() |> ignore
                                        o.AddSchemaTransformer<FSharpUnionSchemaTransformer>() |> ignore)
                                |> ignore)
                        |> ignore)
                    .Build()
            do! host.StartAsync()
            return host
        }
    let webAppCreateSchemaReferenceId (endpoints: Endpoint seq) =
        task {
            let host =
                HostBuilder()
                    .ConfigureWebHost(fun webHostBuilder ->
                        webHostBuilder
                            .UseTestServer()
                            .Configure(fun app ->
                                app
                                    .UseRouting()
                                    .UseEndpoints(fun builder ->
                                        builder.MapOxpeckerEndpoints(endpoints)
                                        builder.MapOpenApi() |> ignore)
                                |> ignore)
                            .ConfigureServices(fun services ->
                                services
                                    .AddRouting()
                                    .AddOpenApi(fun o ->
                                        o.AddSchemaTransformer<FSharpOptionSchemaTransformer>() |> ignore
                                        o.AddSchemaTransformer<FSharpUnionSchemaTransformer>() |> ignore
                                        o.CreateSchemaReferenceId <- _.Type.FullName)
                                |> ignore)
                        |> ignore)
                    .Build()
            do! host.StartAsync()
            return host
        }

type Request1 = { Name: int voption }
type Response1 = { Valid: bool option }

[<Fact>]
let ``Option and voption on primitive types works fine`` () =
    task {
        let endpoints = [
            POST [ route "/" <| text "Hello World" |> addOpenApiSimple<Request1, Response1> ]
        ]
        use! server = WebApp.webApp endpoints
        let client = server.GetTestClient()

        let! result = client.GetAsync("/openapi/v1.json")
        let! resultString = result.Content.ReadAsStringAsync()

        result.StatusCode |> shouldEqual HttpStatusCode.OK
        let expected =
            """{
  "openapi": "3.1.1",
  "info": {
    "title": "Oxpecker.OpenApi.Tests | v1",
    "version": "1.0.0"
  },
  "servers": [
    {
      "url": "http://localhost/"
    }
  ],
  "paths": {
    "/": {
      "post": {
        "tags": [
          "Oxpecker.OpenApi.Tests"
        ],
        "requestBody": {
          "content": {
            "application/json": {
              "schema": {
                "$ref": "#/components/schemas/Request1"
              }
            }
          },
          "required": true
        },
        "responses": {
          "200": {
            "description": "OK",
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/Response1"
                }
              }
            }
          }
        }
      }
    }
  },
  "components": {
    "schemas": {
      "Request1": {
        "required": [
          "name"
        ],
        "type": "object",
        "properties": {
          "name": {
            "pattern": "^-?(?:0|[1-9]\\d*)$",
            "type": [
              "null",
              "integer",
              "string"
            ],
            "format": "int32"
          }
        }
      },
      "Response1": {
        "required": [
          "valid"
        ],
        "type": "object",
        "properties": {
          "valid": {
            "type": [
              "null",
              "boolean"
            ]
          }
        }
      }
    }
  },
  "tags": [
    {
      "name": "Oxpecker.OpenApi.Tests"
    }
  ]
}"""
        resultString.ReplaceLineEndings() |> shouldEqual expected
    }


[<CLIMutable>]
type Response2Inner = { Valid: bool voption }
type Response2 = { Inner: Response2Inner option }

[<Fact>]
let ``nested objects with options work fine`` () =
    task {
        let endpoints = [ GET [ route "/" <| text "Hello World" |> addOpenApiSimple<unit, Response2> ] ]
        use! server = WebApp.webApp endpoints
        let client = server.GetTestClient()

        let! result = client.GetAsync("/openapi/v1.json")
        let! resultString = result.Content.ReadAsStringAsync()

        result.StatusCode |> shouldEqual HttpStatusCode.OK
        let expected =
            """{
  "openapi": "3.1.1",
  "info": {
    "title": "Oxpecker.OpenApi.Tests | v1",
    "version": "1.0.0"
  },
  "servers": [
    {
      "url": "http://localhost/"
    }
  ],
  "paths": {
    "/": {
      "get": {
        "tags": [
          "Oxpecker.OpenApi.Tests"
        ],
        "responses": {
          "200": {
            "description": "OK",
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/Response2"
                }
              }
            }
          }
        }
      }
    }
  },
  "components": {
    "schemas": {
      "Response2": {
        "required": [
          "inner"
        ],
        "type": "object",
        "properties": {
          "inner": {
            "oneOf": [
              {
                "type": "null"
              },
              {
                "$ref": "#/components/schemas/Response2Inner"
              }
            ]
          }
        }
      },
      "Response2Inner": {
        "type": "object",
        "properties": {
          "valid": {
            "type": [
              "null",
              "boolean"
            ]
          }
        }
      }
    }
  },
  "tags": [
    {
      "name": "Oxpecker.OpenApi.Tests"
    }
  ]
}"""
        resultString.ReplaceLineEndings() |> shouldEqual expected
    }


type Request3 = { apple: bool option }
type Response3 = { banana: bool }

[<Fact>]
let ``Issue 87 CreateSchemaReferenceId works well`` () =
    task {
        let endpoints = [
            GET [ route "/" <| text "Hello World" |> addOpenApiSimple<Request3, Response3> ]
        ]
        use! server = WebApp.webAppCreateSchemaReferenceId endpoints
        let client = server.GetTestClient()

        let! result = client.GetAsync("/openapi/v1.json")
        let! resultString = result.Content.ReadAsStringAsync()

        result.StatusCode |> shouldEqual HttpStatusCode.OK
        let expected =
            """{
  "openapi": "3.1.1",
  "info": {
    "title": "Oxpecker.OpenApi.Tests | v1",
    "version": "1.0.0"
  },
  "servers": [
    {
      "url": "http://localhost/"
    }
  ],
  "paths": {
    "/": {
      "get": {
        "tags": [
          "Oxpecker.OpenApi.Tests"
        ],
        "requestBody": {
          "content": {
            "application/json": {
              "schema": {
                "$ref": "#/components/schemas/Oxpecker.OpenApi.Tests.Transformers+Request3"
              }
            }
          },
          "required": true
        },
        "responses": {
          "200": {
            "description": "OK",
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/Oxpecker.OpenApi.Tests.Transformers+Response3"
                }
              }
            }
          }
        }
      }
    }
  },
  "components": {
    "schemas": {
      "Oxpecker.OpenApi.Tests.Transformers+Request3": {
        "required": [
          "apple"
        ],
        "type": "object",
        "properties": {
          "apple": {
            "oneOf": [
              {
                "type": "null"
              },
              {
                "$ref": "#/components/schemas/System.Boolean"
              }
            ]
          }
        }
      },
      "Oxpecker.OpenApi.Tests.Transformers+Response3": {
        "required": [
          "banana"
        ],
        "type": "object",
        "properties": {
          "banana": {
            "$ref": "#/components/schemas/System.Boolean"
          }
        }
      },
      "System.Boolean": {
        "type": "boolean"
      }
    }
  },
  "tags": [
    {
      "name": "Oxpecker.OpenApi.Tests"
    }
  ]
}"""
        resultString.ReplaceLineEndings() |> shouldEqual expected
    }

[<CLIMutable>]
[<Description("Inner type description")>]
type Response4Inner = {
    [<Description("Simple field description")>]
    Valid: bool voption
}
[<Description("Outer type description")>]
type Response4 = {
    [<Description("Nested field description")>]
    Inner: Response4Inner option
}

[<Fact>]
let ``Additional configuration works fine`` () =
    task {
        let endpoints = [
            GET [
                route "/" <| text "Hello World"
                |> addOpenApi(
                    OpenApiConfig(
                        responseBodies = [ ResponseBody(typeof<Response4>) ],
                        configureOperation =
                            fun operation _ _ ->
                                task {
                                    operation.Description <- "Endpoint description"
                                    return operation
                                }
                    )
                )
            ]
        ]
        use! server = WebApp.webApp endpoints
        let client = server.GetTestClient()

        let! result = client.GetAsync("/openapi/v1.json")
        let! resultString = result.Content.ReadAsStringAsync()

        result.StatusCode |> shouldEqual HttpStatusCode.OK
        let expected =
            """{
  "openapi": "3.1.1",
  "info": {
    "title": "Oxpecker.OpenApi.Tests | v1",
    "version": "1.0.0"
  },
  "servers": [
    {
      "url": "http://localhost/"
    }
  ],
  "paths": {
    "/": {
      "get": {
        "tags": [
          "Oxpecker.OpenApi.Tests"
        ],
        "description": "Endpoint description",
        "responses": {
          "200": {
            "description": "OK",
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/Response4"
                }
              }
            }
          }
        }
      }
    }
  },
  "components": {
    "schemas": {
      "Response4": {
        "required": [
          "inner"
        ],
        "type": "object",
        "properties": {
          "inner": {
            "oneOf": [
              {
                "type": "null"
              },
              {
                "description": "Nested field description",
                "$ref": "#/components/schemas/Response4Inner"
              }
            ]
          }
        },
        "description": "Outer type description"
      },
      "Response4Inner": {
        "type": "object",
        "properties": {
          "valid": {
            "type": [
              "null",
              "boolean"
            ],
            "description": "Simple field description"
          }
        },
        "description": "Inner type description"
      }
    }
  },
  "tags": [
    {
      "name": "Oxpecker.OpenApi.Tests"
    }
  ]
}"""
        resultString.ReplaceLineEndings() |> shouldEqual expected
    }

// F# Union Types Tests

type SimpleUnion =
    | Active
    | Inactive
    | Pending

type SimpleUnionResponse = { Status: SimpleUnion }

[<Fact>]
let ``Simple union types generate string enum schema`` () =
    task {
        let endpoints = [
            GET [ route "/" <| text "Hello World" |> addOpenApiSimple<unit, SimpleUnionResponse> ]
        ]
        use! server = WebApp.webApp endpoints
        let client = server.GetTestClient()

        let! result = client.GetAsync("/openapi/v1.json")
        let! resultString = result.Content.ReadAsStringAsync()

        result.StatusCode |> shouldEqual HttpStatusCode.OK
        let expected =
            """{
  "openapi": "3.1.1",
  "info": {
    "title": "Oxpecker.OpenApi.Tests | v1",
    "version": "1.0.0"
  },
  "servers": [
    {
      "url": "http://localhost/"
    }
  ],
  "paths": {
    "/": {
      "get": {
        "tags": [
          "Oxpecker.OpenApi.Tests"
        ],
        "responses": {
          "200": {
            "description": "OK",
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/SimpleUnionResponse"
                }
              }
            }
          }
        }
      }
    }
  },
  "components": {
    "schemas": {
      "SimpleUnion": {
        "enum": [
          "Active",
          "Inactive",
          "Pending"
        ],
        "type": "string",
        "description": "F# union type with values: Active, Inactive, Pending"
      },
      "SimpleUnionResponse": {
        "required": [
          "status"
        ],
        "type": "object",
        "properties": {
          "status": {
            "$ref": "#/components/schemas/SimpleUnion"
          }
        }
      }
    }
  },
  "tags": [
    {
      "name": "Oxpecker.OpenApi.Tests"
    }
  ]
}"""
        resultString.ReplaceLineEndings() |> shouldEqual expected
    }

type ComplexUnion =
    | Circle of Radius: float
    | Rectangle of Width: float * Height: float
    | Point

type ComplexUnionRequest = { Shape: ComplexUnion }

[<Fact>]
let ``Complex union types generate oneOf with discriminator`` () =
    task {
        let endpoints = [
            POST [ route "/" <| text "Hello World" |> addOpenApiSimple<ComplexUnionRequest, unit> ]
        ]
        use! server = WebApp.webApp endpoints
        let client = server.GetTestClient()

        let! result = client.GetAsync("/openapi/v1.json")
        let! resultString = result.Content.ReadAsStringAsync()

        result.StatusCode |> shouldEqual HttpStatusCode.OK
        let expected =
            """{
  "openapi": "3.1.1",
  "info": {
    "title": "Oxpecker.OpenApi.Tests | v1",
    "version": "1.0.0"
  },
  "servers": [
    {
      "url": "http://localhost/"
    }
  ],
  "paths": {
    "/": {
      "post": {
        "tags": [
          "Oxpecker.OpenApi.Tests"
        ],
        "requestBody": {
          "content": {
            "application/json": {
              "schema": {
                "$ref": "#/components/schemas/ComplexUnionRequest"
              }
            }
          },
          "required": true
        },
        "responses": {
          "200": {
            "description": "OK"
          }
        }
      }
    }
  },
  "components": {
    "schemas": {
      "ComplexUnion": {
        "oneOf": [
          {
            "required": [
              "type",
              "radius"
            ],
            "type": "object",
            "properties": {
              "type": {
                "enum": [
                  "Circle"
                ],
                "type": "string"
              },
              "radius": {
                "type": "number",
                "format": "double"
              }
            },
            "description": "Union case: Circle with 1 field(s)"
          },
          {
            "required": [
              "type",
              "width",
              "height"
            ],
            "type": "object",
            "properties": {
              "type": {
                "enum": [
                  "Rectangle"
                ],
                "type": "string"
              },
              "width": {
                "type": "number",
                "format": "double"
              },
              "height": {
                "type": "number",
                "format": "double"
              }
            },
            "description": "Union case: Rectangle with 2 field(s)"
          },
          {
            "required": [
              "type"
            ],
            "type": "object",
            "properties": {
              "type": {
                "enum": [
                  "Point"
                ],
                "type": "string"
              }
            },
            "description": "Union case: Point"
          }
        ],
        "description": "F# discriminated union type with cases: Circle, Rectangle, Point",
        "discriminator": {
          "propertyName": "type",
          "mapping": {
            "Circle": "#/components/schemas/ComplexUnion.Circle",
            "Rectangle": "#/components/schemas/ComplexUnion.Rectangle",
            "Point": "#/components/schemas/ComplexUnion.Point"
          }
        }
      },
      "ComplexUnion.Circle": {
        "required": [
          "type",
          "radius"
        ],
        "type": "object",
        "properties": {
          "type": {
            "enum": [
              "Circle"
            ],
            "type": "string"
          },
          "radius": {
            "type": "number",
            "format": "double"
          }
        },
        "description": "Union case: Circle with 1 field(s)"
      },
      "ComplexUnion.Point": {
        "required": [
          "type"
        ],
        "type": "object",
        "properties": {
          "type": {
            "enum": [
              "Point"
            ],
            "type": "string"
          }
        },
        "description": "Union case: Point"
      },
      "ComplexUnion.Rectangle": {
        "required": [
          "type",
          "width",
          "height"
        ],
        "type": "object",
        "properties": {
          "type": {
            "enum": [
              "Rectangle"
            ],
            "type": "string"
          },
          "width": {
            "type": "number",
            "format": "double"
          },
          "height": {
            "type": "number",
            "format": "double"
          }
        },
        "description": "Union case: Rectangle with 2 field(s)"
      },
      "ComplexUnionRequest": {
        "required": [
          "shape"
        ],
        "type": "object",
        "properties": {
          "shape": {
            "$ref": "#/components/schemas/ComplexUnion"
          }
        }
      }
    }
  },
  "tags": [
    {
      "name": "Oxpecker.OpenApi.Tests"
    }
  ]
}"""
        resultString.ReplaceLineEndings() |> shouldEqual expected
    }

type UnionWithPrimitives =
    | IntValue of Value: int
    | StringValue of Value: string
    | BoolValue of Value: bool
    | GuidValue of Value: Guid

type UnionWithPrimitivesResponse = { Data: UnionWithPrimitives }

[<Fact>]
let ``Union with various primitive types maps correctly`` () =
    task {
        let endpoints = [
            GET [ route "/" <| text "Hello World" |> addOpenApiSimple<unit, UnionWithPrimitivesResponse> ]
        ]
        use! server = WebApp.webApp endpoints
        let client = server.GetTestClient()

        let! result = client.GetAsync("/openapi/v1.json")
        let! resultString = result.Content.ReadAsStringAsync()

        result.StatusCode |> shouldEqual HttpStatusCode.OK
        let expected =
            """{
  "openapi": "3.1.1",
  "info": {
    "title": "Oxpecker.OpenApi.Tests | v1",
    "version": "1.0.0"
  },
  "servers": [
    {
      "url": "http://localhost/"
    }
  ],
  "paths": {
    "/": {
      "get": {
        "tags": [
          "Oxpecker.OpenApi.Tests"
        ],
        "responses": {
          "200": {
            "description": "OK",
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/UnionWithPrimitivesResponse"
                }
              }
            }
          }
        }
      }
    }
  },
  "components": {
    "schemas": {
      "UnionWithPrimitives": {
        "oneOf": [
          {
            "required": [
              "type",
              "value"
            ],
            "type": "object",
            "properties": {
              "type": {
                "enum": [
                  "IntValue"
                ],
                "type": "string"
              },
              "value": {
                "type": "integer",
                "format": "int32"
              }
            },
            "description": "Union case: IntValue with 1 field(s)"
          },
          {
            "required": [
              "type",
              "value"
            ],
            "type": "object",
            "properties": {
              "type": {
                "enum": [
                  "StringValue"
                ],
                "type": "string"
              },
              "value": {
                "type": "string"
              }
            },
            "description": "Union case: StringValue with 1 field(s)"
          },
          {
            "required": [
              "type",
              "value"
            ],
            "type": "object",
            "properties": {
              "type": {
                "enum": [
                  "BoolValue"
                ],
                "type": "string"
              },
              "value": {
                "type": "boolean"
              }
            },
            "description": "Union case: BoolValue with 1 field(s)"
          },
          {
            "required": [
              "type",
              "value"
            ],
            "type": "object",
            "properties": {
              "type": {
                "enum": [
                  "GuidValue"
                ],
                "type": "string"
              },
              "value": {
                "type": "string",
                "format": "uuid"
              }
            },
            "description": "Union case: GuidValue with 1 field(s)"
          }
        ],
        "description": "F# discriminated union type with cases: IntValue, StringValue, BoolValue, GuidValue",
        "discriminator": {
          "propertyName": "type",
          "mapping": {
            "IntValue": "#/components/schemas/UnionWithPrimitives.IntValue",
            "StringValue": "#/components/schemas/UnionWithPrimitives.StringValue",
            "BoolValue": "#/components/schemas/UnionWithPrimitives.BoolValue",
            "GuidValue": "#/components/schemas/UnionWithPrimitives.GuidValue"
          }
        }
      },
      "UnionWithPrimitives.BoolValue": {
        "required": [
          "type",
          "value"
        ],
        "type": "object",
        "properties": {
          "type": {
            "enum": [
              "BoolValue"
            ],
            "type": "string"
          },
          "value": {
            "type": "boolean"
          }
        },
        "description": "Union case: BoolValue with 1 field(s)"
      },
      "UnionWithPrimitives.GuidValue": {
        "required": [
          "type",
          "value"
        ],
        "type": "object",
        "properties": {
          "type": {
            "enum": [
              "GuidValue"
            ],
            "type": "string"
          },
          "value": {
            "type": "string",
            "format": "uuid"
          }
        },
        "description": "Union case: GuidValue with 1 field(s)"
      },
      "UnionWithPrimitives.IntValue": {
        "required": [
          "type",
          "value"
        ],
        "type": "object",
        "properties": {
          "type": {
            "enum": [
              "IntValue"
            ],
            "type": "string"
          },
          "value": {
            "type": "integer",
            "format": "int32"
          }
        },
        "description": "Union case: IntValue with 1 field(s)"
      },
      "UnionWithPrimitives.StringValue": {
        "required": [
          "type",
          "value"
        ],
        "type": "object",
        "properties": {
          "type": {
            "enum": [
              "StringValue"
            ],
            "type": "string"
          },
          "value": {
            "type": "string"
          }
        },
        "description": "Union case: StringValue with 1 field(s)"
      },
      "UnionWithPrimitivesResponse": {
        "required": [
          "data"
        ],
        "type": "object",
        "properties": {
          "data": {
            "$ref": "#/components/schemas/UnionWithPrimitives"
          }
        }
      }
    }
  },
  "tags": [
    {
      "name": "Oxpecker.OpenApi.Tests"
    }
  ]
}"""
        resultString.ReplaceLineEndings() |> shouldEqual expected
    }

type RecordPayload = { Id: Guid; Name: string }

type NestedUnion =
    | Alpha
    | Beta of Code: int

type UnionWithComplexFields =
    | WithRecord of Payload: RecordPayload
    | WithNestedUnion of Choice: NestedUnion
    | WithBoth of Payload: RecordPayload * Choice: NestedUnion

type UnionWithComplexFieldsRequest = { Data: UnionWithComplexFields }
type UnionWithComplexFieldsResponse = { Data: UnionWithComplexFields }

[<Fact>]
let ``Union with record and nested union fields maps correctly`` () =
    task {
        let endpoints = [
            POST [ route "/" <| text "Hello World" |> addOpenApiSimple<UnionWithComplexFieldsRequest, UnionWithComplexFieldsResponse> ]
        ]
        use! server = WebApp.webApp endpoints
        let client = server.GetTestClient()

        let! result = client.GetAsync("/openapi/v1.json")
        let! resultString = result.Content.ReadAsStringAsync()

        result.StatusCode |> shouldEqual HttpStatusCode.OK
        let expected =
            """{
  "openapi": "3.1.1",
  "info": {
    "title": "Oxpecker.OpenApi.Tests | v1",
    "version": "1.0.0"
  },
  "servers": [
    {
      "url": "http://localhost/"
    }
  ],
  "paths": {
    "/": {
      "post": {
        "tags": [
          "Oxpecker.OpenApi.Tests"
        ],
        "requestBody": {
          "content": {
            "application/json": {
              "schema": {
                "$ref": "#/components/schemas/UnionWithComplexFieldsRequest"
              }
            }
          },
          "required": true
        },
        "responses": {
          "200": {
            "description": "OK",
            "content": {
              "application/json": {
                "schema": {
                  "$ref": "#/components/schemas/UnionWithComplexFieldsResponse"
                }
              }
            }
          }
        }
      }
    }
  },
  "components": {
    "schemas": {
      "NestedUnion": {
        "oneOf": [
          {
            "required": [
              "type"
            ],
            "type": "object",
            "properties": {
              "type": {
                "enum": [
                  "Alpha"
                ],
                "type": "string"
              }
            },
            "description": "Union case: Alpha"
          },
          {
            "required": [
              "type",
              "code"
            ],
            "type": "object",
            "properties": {
              "type": {
                "enum": [
                  "Beta"
                ],
                "type": "string"
              },
              "code": {
                "type": "integer",
                "format": "int32"
              }
            },
            "description": "Union case: Beta with 1 field(s)"
          }
        ],
        "description": "F# discriminated union type with cases: Alpha, Beta",
        "discriminator": {
          "propertyName": "type",
          "mapping": {
            "Alpha": "#/components/schemas/NestedUnion.Alpha",
            "Beta": "#/components/schemas/NestedUnion.Beta"
          }
        }
      },
      "NestedUnion.Alpha": {
        "required": [
          "type"
        ],
        "type": "object",
        "properties": {
          "type": {
            "enum": [
              "Alpha"
            ],
            "type": "string"
          }
        },
        "description": "Union case: Alpha"
      },
      "NestedUnion.Beta": {
        "required": [
          "type",
          "code"
        ],
        "type": "object",
        "properties": {
          "type": {
            "enum": [
              "Beta"
            ],
            "type": "string"
          },
          "code": {
            "type": "integer",
            "format": "int32"
          }
        },
        "description": "Union case: Beta with 1 field(s)"
      },
      "RecordPayload": {
        "required": [
          "id",
          "name"
        ],
        "type": "object",
        "properties": {
          "id": {
            "type": "string",
            "format": "uuid"
          },
          "name": {
            "type": "string"
          }
        }
      },
      "UnionWithComplexFields": {
        "oneOf": [
          {
            "required": [
              "type",
              "payload"
            ],
            "type": "object",
            "properties": {
              "type": {
                "enum": [
                  "WithRecord"
                ],
                "type": "string"
              },
              "payload": {
                "$ref": "#/components/schemas/RecordPayload"
              }
            },
            "description": "Union case: WithRecord with 1 field(s)"
          },
          {
            "required": [
              "type",
              "choice"
            ],
            "type": "object",
            "properties": {
              "type": {
                "enum": [
                  "WithNestedUnion"
                ],
                "type": "string"
              },
              "choice": {
                "$ref": "#/components/schemas/NestedUnion"
              }
            },
            "description": "Union case: WithNestedUnion with 1 field(s)"
          },
          {
            "required": [
              "type",
              "payload",
              "choice"
            ],
            "type": "object",
            "properties": {
              "type": {
                "enum": [
                  "WithBoth"
                ],
                "type": "string"
              },
              "payload": {
                "$ref": "#/components/schemas/RecordPayload"
              },
              "choice": {
                "$ref": "#/components/schemas/NestedUnion"
              }
            },
            "description": "Union case: WithBoth with 2 field(s)"
          }
        ],
        "description": "F# discriminated union type with cases: WithRecord, WithNestedUnion, WithBoth",
        "discriminator": {
          "propertyName": "type",
          "mapping": {
            "WithRecord": "#/components/schemas/UnionWithComplexFields.WithRecord",
            "WithNestedUnion": "#/components/schemas/UnionWithComplexFields.WithNestedUnion",
            "WithBoth": "#/components/schemas/UnionWithComplexFields.WithBoth"
          }
        }
      },
      "UnionWithComplexFields.WithBoth": {
        "required": [
          "type",
          "payload",
          "choice"
        ],
        "type": "object",
        "properties": {
          "type": {
            "enum": [
              "WithBoth"
            ],
            "type": "string"
          },
          "payload": {
            "$ref": "#/components/schemas/RecordPayload"
          },
          "choice": {
            "$ref": "#/components/schemas/NestedUnion"
          }
        },
        "description": "Union case: WithBoth with 2 field(s)"
      },
      "UnionWithComplexFields.WithNestedUnion": {
        "required": [
          "type",
          "choice"
        ],
        "type": "object",
        "properties": {
          "type": {
            "enum": [
              "WithNestedUnion"
            ],
            "type": "string"
          },
          "choice": {
            "$ref": "#/components/schemas/NestedUnion"
          }
        },
        "description": "Union case: WithNestedUnion with 1 field(s)"
      },
      "UnionWithComplexFields.WithRecord": {
        "required": [
          "type",
          "payload"
        ],
        "type": "object",
        "properties": {
          "type": {
            "enum": [
              "WithRecord"
            ],
            "type": "string"
          },
          "payload": {
            "$ref": "#/components/schemas/RecordPayload"
          }
        },
        "description": "Union case: WithRecord with 1 field(s)"
      },
      "UnionWithComplexFieldsRequest": {
        "required": [
          "data"
        ],
        "type": "object",
        "properties": {
          "data": {
            "$ref": "#/components/schemas/UnionWithComplexFields"
          }
        }
      },
      "UnionWithComplexFieldsResponse": {
        "required": [
          "data"
        ],
        "type": "object",
        "properties": {
          "data": {
            "$ref": "#/components/schemas/UnionWithComplexFields"
          }
        }
      }
    }
  },
  "tags": [
    {
      "name": "Oxpecker.OpenApi.Tests"
    }
  ]
}"""
        resultString.ReplaceLineEndings() |> shouldEqual expected
    }
