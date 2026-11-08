#region Related components
using System;
using System.Linq;
using System.Dynamic;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services.MCP
{
	public static class McpExtensions
	{
		static Dictionary<Type, string> JsonSchemaTypes { get; } = new Dictionary<Type, string>()
		{
			{ typeof(string), "string" },
			{ typeof(char), "string" },
			{ typeof(char?), "string|null" },
			{ typeof(byte), "integer" },
			{ typeof(byte?), "integer|null" },
			{ typeof(sbyte), "integer" },
			{ typeof(sbyte?), "integer|null" },
			{ typeof(short), "integer" },
			{ typeof(short?), "integer|null" },
			{ typeof(ushort), "integer" },
			{ typeof(ushort?), "integer|null" },
			{ typeof(int), "integer" },
			{ typeof(int?), "integer|null" },
			{ typeof(uint), "integer" },
			{ typeof(uint?), "integer|null" },
			{ typeof(long), "integer" },
			{ typeof(long?), "integer|null" },
			{ typeof(ulong), "integer" },
			{ typeof(ulong?), "integer|null" },
			{ typeof(float), "number" },
			{ typeof(float?), "number|null" },
			{ typeof(double), "number" },
			{ typeof(double?), "number|null" },
			{ typeof(decimal), "number" },
			{ typeof(decimal?), "number|null" },
			{ typeof(DateTime), "string/format:date-time" },
			{ typeof(DateTime?), "string|null/format:date-time" },
			{ typeof(DateTimeOffset), "string/format:date-time" },
			{ typeof(DateTimeOffset?), "string|null/format:date-time" },
			{ typeof(bool), "boolean" },
			{ typeof(bool?), "boolean|null" },
			{ typeof(Guid), "string/format:uuid" },
			{ typeof(Guid?), "string|null/format:uuid" },
			{ typeof(byte[]), "string/contentEncoding:base64" }
		};

		static bool IsJsonSchemaNullable(this AttributeInfo attribute, PropertyAttribute propertyInfo = null)
		{
			if (Nullable.GetUnderlyingType(attribute.Type) != null)
				return true;

			if (attribute.Type.IsValueType)
				return false;

			propertyInfo = propertyInfo ?? attribute.GetCustomAttribute<PropertyAttribute>();
			return propertyInfo == null || !(propertyInfo.NotNull || propertyInfo.NotEmpty || attribute.NotNull || (attribute.NotEmpty != null && attribute.NotEmpty.Value));
		}

		static bool IsJsonSchemaRequired(this AttributeInfo attribute, PropertyAttribute propertyInfo = null, FormControlAttribute controlInfo = null)
		{
			if (attribute.GetCustomAttribute<PrimaryKeyAttribute>() != null)
				return true;

			propertyInfo = propertyInfo ?? attribute.GetCustomAttribute<PropertyAttribute>();
			controlInfo = controlInfo ?? attribute.GetCustomAttribute<FormControlAttribute>();
			return propertyInfo?.NotNull == true || propertyInfo?.NotEmpty == true || attribute.NotNull || (attribute.NotEmpty != null && attribute.NotEmpty.Value) || controlInfo?.Required == true;
		}

		static JObject ToJsonSchema(this Type type, bool allowNull = false, string format = null, int maxLength = 0, int minLength = 0, object maxValue = null, object minValue = null)
		{
			var schema = new JObject();
			if (McpExtensions.JsonSchemaTypes.TryGetValue(type, out var definition))
			{
				var parts = definition.ToArray("/", true);
				if (parts == null || parts.Length < 1)
					return schema;

				var types = parts[0].ToArray("|", true);

				schema["type"] = types.Length == 1
					? allowNull ? new JArray(types[0], "null") : new JValue(types[0]) as JToken
					: new JArray(types[0], types[1]);

				if (parts.Length > 1)
				{
					var additional = parts[1].ToArray(":", true);
					if (additional.Length > 1)
						schema[additional[0]] = additional[1];
				}

				if (!string.IsNullOrWhiteSpace(format))
					schema["format"] = format;

				if (maxLength > 0)
					schema["maxLength"] = maxLength;

				if (minLength > 0)
					schema["minLength"] = minLength;

				if (maxValue != null)
					schema["maximum"] = new JValue(maxValue);

				if (minValue != null)
					schema["minimum"] = new JValue(minValue);
			}
			return schema;
		}

		static (JObject Properties, JArray Required) ToJsonSchema(this IEnumerable<AttributeInfo> attributes, ExpandoObject localization = null)
		{
			var properties = new JObject();
			var required = new JArray();
			attributes.ForEach(attribute =>
			{
				var propertySchema = new JObject();

				var propertyInfo = attribute.GetCustomAttribute<PropertyAttribute>();
				var controlInfo = attribute.GetCustomAttribute<FormControlAttribute>();
				var schemaInfo = attribute.GetCustomAttribute<McpResourceJsonSchemaAttribute>();

				if (schemaInfo == null || !schemaInfo.Ignore)
				{
					if (attribute.IsEnum() || attribute.IsStringEnum())
					{
						var underlyingEnumType = Nullable.GetUnderlyingType(attribute.Type);
						var enums = string.IsNullOrWhiteSpace(schemaInfo?.EnumValues)
							? Enum.GetValues(underlyingEnumType ?? attribute.Type).ToEnumerable().Select(@enum => @enum.ToString()).ToJArray()
							: schemaInfo.EnumValues.ToArray(";", true).ToJArray();
						if (underlyingEnumType != null)
							enums.Add(null);
						propertySchema = new JObject
						{
							["type"] = underlyingEnumType != null ? new JArray("string", "null") : new JValue("string") as JToken,
							["enum"] = enums
						};
					}

					else if (attribute.IsGenericListOrHashSet())
					{
						var items = new JObject();
						var genericType = attribute.GetFirstGenericTypeArgument();
						if (genericType.IsClassType())
						{
							var (itemProperties, itemRequired) = genericType.GetPublicAttributes(attr => !attr.IsStatic && attr.GetCustomAttribute<IgnoreAttribute>() == null).Select(attr => new AttributeInfo(attr)).ToJsonSchema(localization);
							items = new JObject
							{
								["type"] = "object",
								["properties"] = itemProperties,
								["required"] = itemRequired,
								["additionalProperties"] = false
							};
						}
						else if (genericType.IsEnum)
							items = new JObject
							{
								["type"] = "string",
								["enum"] = Enum.GetValues(genericType).ToEnumerable().Select(value => value.ToString()).ToJArray()
							};
						else
							items = genericType.ToJsonSchema();
						propertySchema = new JObject
						{
							["type"] = attribute.IsJsonSchemaNullable() ? new JArray("array", "null") : new JValue("array") as JToken,
							["items"] = items
						};
					}

					else
					{
						var allowNull = attribute.IsJsonSchemaNullable(propertyInfo);
						var format = "DatePicker".IsEquals(controlInfo?.ControlType)
							? controlInfo.DatePickerWithTimes ? "date-time" : "date"
							: "URL".IsEquals(controlInfo?.DataType) || "URI".IsEquals(controlInfo?.DataType) ? "uri" : null;
						var maxLength = propertyInfo != null ? propertyInfo.MaxLength : 0;
						var minLength = propertyInfo != null ? propertyInfo.MinLength : 0;
						var minValue = (propertyInfo?.MinValue ?? attribute?.MinValue) as object;
						if (minValue != null)
							try
							{
								if (attribute.IsIntegralType())
									minValue = minValue.CastAs<long>();
								else if (attribute.IsFloatingPointType())
									minValue = minValue.CastAs<double>();
							}
							catch { }
						var maxValue = (propertyInfo?.MaxValue ?? attribute?.MaxValue) as object;
						if (maxValue != null)
							try
							{
								if (attribute.IsIntegralType())
									maxValue = maxValue.CastAs<long>();
								else if (attribute.IsFloatingPointType())
									maxValue = maxValue.CastAs<double>();
							}
							catch { }
						propertySchema = attribute.Type.ToJsonSchema(allowNull, format, maxLength, minLength, maxValue, minValue);
					}
				}

				if (propertySchema.Count > 0)
				{
					var title = schemaInfo?.Title ?? controlInfo?.Label?.Trim();
					if (title.IsStartsWith("{{") && title.IsEndsWith("}}"))
					{
						title = title.Replace(StringComparison.OrdinalIgnoreCase, "[name]", attribute.Name).Replace(StringComparison.OrdinalIgnoreCase, "[nameLower]", attribute.Name.ToLower());
						title = title.Replace(StringComparison.OrdinalIgnoreCase, "[type]", attribute.Type.GetTypeName(true)).Replace(StringComparison.OrdinalIgnoreCase, "[typeLower]", attribute.Type.GetTypeName(true).ToLower());
						title = localization?.Get<string>(title.Replace("{{", "").Replace("}}", ""));
					}
					if (!string.IsNullOrWhiteSpace(title))
						propertySchema["title"] = title;
					var description = schemaInfo?.Description ?? controlInfo?.Description?.Trim();
					if (description.IsStartsWith("{{") && description.IsEndsWith("}}"))
					{
						description = description.Replace(StringComparison.OrdinalIgnoreCase, "[name]", attribute.Name).Replace(StringComparison.OrdinalIgnoreCase, "[nameLower]", attribute.Name.ToLower());
						description = description.Replace(StringComparison.OrdinalIgnoreCase, "[type]", attribute.Type.GetTypeName(true)).Replace(StringComparison.OrdinalIgnoreCase, "[typeLower]", attribute.Type.GetTypeName(true).ToLower());
						description = localization?.Get<string>(description.Replace("{{", "").Replace("}}", ""));
					}
					if (!string.IsNullOrWhiteSpace(description))
						propertySchema["description"] = description;
					properties[attribute.Name] = propertySchema;
					if (attribute.IsJsonSchemaRequired(propertyInfo, controlInfo))
						required.Add(attribute.Name);
				}
			});
			return (properties, required);
		}

		/// <summary>
		/// Generates the JSON schema of this type for working with input
		/// </summary>
		/// <param name="type"></param>
		/// <param name="localization"></param>
		/// <param name="onCompleted"></param>
		/// <returns></returns>
		public static JObject GenerateInputJsonSchema(this Type type, ExpandoObject localization = null, Action<JObject> onCompleted = null)
		{
			var entityDefinition = type.GetEntityDefinition() ?? throw new InformationInvalidException("The type is invalid (no entity definition)");

			var resourceInfo = type.GetCustomAttribute<McpResourceAttribute>();
			if (resourceInfo != null && resourceInfo.Ignore)
				return null;

			var attributes = type.GetPublicAttributes(attribute => !attribute.IsStatic && attribute.GetCustomAttribute<IgnoreAttribute>() == null).Select(attribute => new AttributeInfo(attribute)).Where(attribute =>
			{
				var primaryKey = attribute.GetCustomAttribute<PrimaryKeyAttribute>();
				var schemaJson = attribute.GetCustomAttribute<McpResourceJsonSchemaAttribute>();
				return primaryKey == null && (schemaJson == null || !schemaJson.IgnoreInput);
			});
			var (properties, required) = attributes.ToJsonSchema(localization);

			var serviceName = entityDefinition.GetServiceName();
			var objectName = entityDefinition.GetObjectName(false);

			var entityInfo = type.GetCustomAttribute<EntityAttribute>();
			var schemaInfo = type.GetCustomAttribute<McpResourceAttribute>();

			var title = schemaInfo?.Title ?? entityInfo.Title ?? objectName;
			if (title.IsStartsWith("{{") && title.IsEndsWith("}}"))
				title = localization?.Get<string>(title.Replace("{{", "").Replace("}}", ""));

			var description = schemaInfo?.Description ?? entityInfo.Description ?? $"{objectName} in the {serviceName} service";
			if (description.IsStartsWith("{{") && description.IsEndsWith("}}"))
				description = localization?.Get<string>(description.Replace("{{", "").Replace("}}", ""));

			var schema = new JObject
			{
				["$id"] = $"vieapps://schemas/{serviceName.ToLower()}/{objectName.ToLower()}/input",
				["title"] = title,
				["description"] = description,
				["type"] = "object",
				["properties"] = properties,
				["required"] = required,
				["additionalProperties"] = false
			};

			onCompleted?.Invoke(schema);
			return schema;
		}

		/// <summary>
		/// Generates the JSON schema of this type for working with output
		/// </summary>
		/// <param name="type"></param>
		/// <param name="localization"></param>
		/// <param name="onCompleted"></param>
		/// <returns></returns>
		public static JObject GenerateOutputJsonSchema(this Type type, ExpandoObject localization = null, Action<JObject> onCompleted = null)
		{
			var entityDefinition = type.GetEntityDefinition() ?? throw new InformationInvalidException("The type is invalid (no entity definition)");

			var resourceInfo = type.GetCustomAttribute<McpResourceAttribute>();
			if (resourceInfo != null && resourceInfo.Ignore)
				return null;

			var attributes = type.GetPublicAttributes(attribute => !attribute.IsStatic && attribute.GetCustomAttribute<IgnoreAttribute>() == null).Select(attribute => new AttributeInfo(attribute)).Where(attribute =>
			{
				var schemaJson = attribute.GetCustomAttribute<McpResourceJsonSchemaAttribute>();
				return schemaJson == null || !schemaJson.IgnoreOutput;
			});
			var (properties, required) = attributes.ToJsonSchema(localization);

			var serviceName = entityDefinition.GetServiceName();
			var objectName = entityDefinition.GetObjectName(false);

			var entityInfo = type.GetCustomAttribute<EntityAttribute>();
			var schemaInfo = type.GetCustomAttribute<McpResourceAttribute>();

			var title = schemaInfo?.Title ?? entityInfo.Title ?? objectName;
			if (title.IsStartsWith("{{") && title.IsEndsWith("}}"))
				title = localization?.Get<string>(title.Replace("{{", "").Replace("}}", ""));

			var description = schemaInfo?.Description ?? entityInfo.Description ?? $"{objectName} in the {serviceName} service";
			if (description.IsStartsWith("{{") && description.IsEndsWith("}}"))
				description = localization?.Get<string>(description.Replace("{{", "").Replace("}}", ""));

			var schema = new JObject
			{
				["$id"] = $"vieapps://schemas/{serviceName.ToLower()}/{objectName.ToLower()}/output",
				["title"] = title,
				["description"] = description,
				["type"] = "object",
				["properties"] = properties,
				["required"] = required,
				["additionalProperties"] = false
			};

			onCompleted?.Invoke(schema);
			return schema;
		}

		static JObject GenerateSearchingOperatorsJsonSchema(this AttributeInfo attribute)
		{
			var propertyInfo = attribute.GetCustomAttribute<PropertyAttribute>();
			var controlInfo = attribute.GetCustomAttribute<FormControlAttribute>();
			var schemaInfo = attribute.GetCustomAttribute<McpResourceJsonSchemaAttribute>();

			var allowNull = attribute.IsJsonSchemaNullable(propertyInfo);

			var format = "DatePicker".IsEquals(controlInfo?.ControlType)
				? controlInfo.DatePickerWithTimes ? "date-time" : "date"
				: "URL".IsEquals(controlInfo?.DataType) || "URI".IsEquals(controlInfo?.DataType) ? "uri" : null;

			var maxLength = propertyInfo?.MaxLength ?? 0;
			var minLength = propertyInfo?.MinLength ?? 0;

			var minValue = (propertyInfo?.MinValue ?? attribute.MinValue) as object;
			if (minValue != null)
				try
				{
					if (attribute.IsIntegralType())
						minValue = minValue.CastAs<long>();
					else if (attribute.IsFloatingPointType())
						minValue = minValue.CastAs<double>();
				}
				catch { }

			var maxValue = (propertyInfo?.MaxValue ?? attribute.MaxValue) as object;
			if (maxValue != null)
				try
				{
					if (attribute.IsIntegralType())
						maxValue = maxValue.CastAs<long>();
					else if (attribute.IsFloatingPointType())
						maxValue = maxValue.CastAs<double>();
				}
				catch { }

			JObject valueSchema;
			if (attribute.IsEnum() || attribute.IsStringEnum())
			{
				var underlyingEnumType = Nullable.GetUnderlyingType(attribute.Type);
				var enums = string.IsNullOrWhiteSpace(schemaInfo?.EnumValues)
					? Enum.GetValues(underlyingEnumType ?? attribute.Type).ToEnumerable().Select(@enum => @enum.ToString()).ToJArray()
					: schemaInfo.EnumValues.ToArray(";", true).ToJArray();
				if (underlyingEnumType != null)
					enums.Add(null);
				valueSchema = new JObject
				{
					["type"] = underlyingEnumType != null ? new JArray("string", "null") : new JValue("string") as JToken,
					["enum"] = enums
				};
			}
			else
				valueSchema = attribute.Type.ToJsonSchema(allowNull, format, maxLength, minLength, maxValue, minValue);

			var operators = new JObject
			{
				[nameof(CompareOperator.Equals)] = valueSchema.DeepClone(),
				[nameof(CompareOperator.NotEquals)] = valueSchema.DeepClone()
			};

			if (attribute.IsIntegralType() || attribute.IsFloatingPointType() || attribute.IsDateTimeType())
			{
				operators[nameof(CompareOperator.LessThan)] = valueSchema.DeepClone();
				operators[nameof(CompareOperator.LessThanOrEquals)] = valueSchema.DeepClone();
				operators[nameof(CompareOperator.Greater)] = valueSchema.DeepClone();
				operators[nameof(CompareOperator.GreaterOrEquals)] = valueSchema.DeepClone();
			}

			if (attribute.Type == typeof(string))
			{
				operators[nameof(CompareOperator.Contains)] = valueSchema.DeepClone();
				operators[nameof(CompareOperator.StartsWith)] = valueSchema.DeepClone();
				operators[nameof(CompareOperator.EndsWith)] = valueSchema.DeepClone();

				operators[nameof(CompareOperator.IsEmpty)] = new JObject
				{
					["type"] = "boolean"
				};

				operators[nameof(CompareOperator.IsNotEmpty)] = new JObject
				{
					["type"] = "boolean"
				};
			}

			if (allowNull)
			{
				operators[nameof(CompareOperator.IsNull)] = new JObject
				{
					["type"] = "boolean"
				};

				operators[nameof(CompareOperator.IsNotNull)] = new JObject
				{
					["type"] = "boolean"
				};
			}

			return new JObject
			{
				["type"] = "object",
				["properties"] = operators,
				["additionalProperties"] = false
			};
		}

		/// <summary>
		/// Generates the JSON schema of this type for working with searching input
		/// </summary>
		/// <param name="type"></param>
		/// <param name="localization"></param>
		/// <param name="onCompleted"></param>
		/// <returns></returns>
		public static JObject GenerateSearchingInputJsonSchema(this Type type, ExpandoObject localization = null, Action<JObject> onCompleted = null)
		{
			var entityDefinition = type.GetEntityDefinition() ?? throw new InformationInvalidException("The type is invalid (no entity definition)");

			var resourceInfo = type.GetCustomAttribute<McpResourceAttribute>();
			if (resourceInfo != null && (resourceInfo.Ignore || (resourceInfo.ExcludedTools ?? "").IsContains("Search")))
				return null;

			var entityInfo = type.GetCustomAttribute<EntityAttribute>();
			var schemaInfo = type.GetCustomAttribute<McpResourceAttribute>();

			var attributes = type.GetPublicAttributes(attribute => !attribute.IsStatic && attribute.GetCustomAttribute<IgnoreAttribute>() == null).Select(attribute => new AttributeInfo(attribute)).ToList();

			var filterableAttributes = attributes.Where(attribute =>
			{
				var sortableInfo = attribute.GetCustomAttribute<SortableAttribute>();
				var attributeSchemaInfo = attribute.GetCustomAttribute<McpResourceJsonSchemaAttribute>();
				return sortableInfo != null && attributeSchemaInfo?.IgnoreSearch != true;
			}).ToList();

			var filterConditions = new JArray();
			filterableAttributes.ForEach(attribute =>
			{
				filterConditions.Add(new JObject
				{
					["type"] = "object",
					["properties"] = new JObject
					{
						[attribute.Name] = attribute.GenerateSearchingOperatorsJsonSchema()
					},
					["required"] = new JArray(attribute.Name),
					["additionalProperties"] = false
				});
			});

			var filterByProperties = entityInfo.Searchable
				? new JObject
				{
					["Query"] = new JObject
					{
						["type"] = new JArray("string", "null"),
						["maxLength"] = 250,
						["description"] = "Free-text query applied to searchable properties."
					}
				}
				: new JObject();

			if (filterConditions.Count > 0)
				filterByProperties["And"] = new JObject
				{
					["type"] = new JArray("array", "null"),
					["items"] = new JObject
					{
						["oneOf"] = filterConditions
					}
				};

			var filterBy = new JObject
			{
				["type"] = new JArray("object", "null"),
				["properties"] = filterByProperties,
				["additionalProperties"] = false
			};

			var sortableAttributes = attributes.Where(attribute =>
			{
				var sortableInfo = attribute.GetCustomAttribute<SortableAttribute>();
				var attributeSchemaInfo = attribute.GetCustomAttribute<McpResourceJsonSchemaAttribute>();
				return sortableInfo != null && attributeSchemaInfo?.IgnoreSort != true;
			}).ToList();

			var sortProperties = new JObject();
			sortableAttributes.ForEach(attribute =>
			{
				sortProperties[attribute.Name] = new JObject
				{
					["type"] = "string",
					["enum"] = new JArray("Ascending", "Descending")
				};
			});

			var sortBy = new JObject
			{
				["type"] = new JArray("object", "null"),
				["properties"] = sortProperties,
				["additionalProperties"] = false
			};

			var pagination = new JObject
			{
				["type"] = new JArray("object", "null"),
				["properties"] = new JObject
				{
					["PageSize"] = new JObject
					{
						["type"] = "integer",
						["minimum"] = 1
					}
				},
				["additionalProperties"] = false
			};

			var searchProperties = new JObject();

			if (filterByProperties.Count > 0)
				searchProperties["FilterBy"] = filterBy;

			if (sortProperties.Count > 0)
				searchProperties["SortBy"] = sortBy;

			searchProperties["Pagination"] = pagination;

			var initialSearch = new JObject
			{
				["type"] = "object",
				["properties"] = searchProperties,
				["additionalProperties"] = false
			};

			var cursorSearch = new JObject
			{
				["type"] = "object",
				["properties"] = new JObject
				{
					["cursor"] = new JObject
					{
						["type"] = "string",
						["minLength"] = 1,
						["description"] = "Opaque cursor returned by the previous search result."
					}
				},
				["required"] = new JArray("cursor"),
				["additionalProperties"] = false
			};

			var serviceName = entityDefinition.GetServiceName();
			var objectName = entityDefinition.GetObjectName(false);

			var title = schemaInfo?.Title ?? entityInfo.Title ?? objectName;
			if (title.IsStartsWith("{{") && title.IsEndsWith("}}"))
				title = localization?.Get<string>(title.Replace("{{", "").Replace("}}", ""));

			var description = schemaInfo?.Description ?? entityInfo.Description ?? $"{objectName} in the {serviceName} service";
			if (description.IsStartsWith("{{") && description.IsEndsWith("}}"))
				description = localization?.Get<string>(description.Replace("{{", "").Replace("}}", ""));

			var schema = new JObject
			{
				["$id"] = $"vieapps://schemas/{serviceName.ToLower()}/{objectName.ToLower()}/search-input",
				["title"] = $"Search {title}",
				["description"] = $"Search {description}",
				["type"] = "object",
				["oneOf"] = new JArray(initialSearch, cursorSearch)
			};

			onCompleted?.Invoke(schema);
			return schema;
		}

		/// <summary>
		/// Generates the JSON schema of this type for working with search output
		/// </summary>
		/// <param name="type"></param>
		/// <param name="localization"></param>
		/// <param name="onCompleted"></param>
		/// <returns></returns>
		public static JObject GenerateSearchingOutputJsonSchema(this Type type, ExpandoObject localization = null, Action<JObject> onCompleted = null)
		{
			var entityDefinition = type.GetEntityDefinition() ?? throw new InformationInvalidException("The type is invalid (no entity definition)");

			var resourceInfo = type.GetCustomAttribute<McpResourceAttribute>();
			if (resourceInfo != null && (resourceInfo.Ignore || (resourceInfo.ExcludedTools ?? "").IsContains("Search")))
				return null;

			var outputSchema = type.GenerateOutputJsonSchema(localization);
			if (outputSchema == null)
				return null;

			outputSchema.Remove("$id");

			var serviceName = entityDefinition.GetServiceName();
			var objectName = entityDefinition.GetObjectName(false);

			var entityInfo = type.GetCustomAttribute<EntityAttribute>();
			var schemaInfo = type.GetCustomAttribute<McpResourceAttribute>();

			var title = schemaInfo?.Title ?? entityInfo.Title ?? objectName;
			if (title.IsStartsWith("{{") && title.IsEndsWith("}}"))
				title = localization?.Get<string>(title.Replace("{{", "").Replace("}}", ""));

			var description = schemaInfo?.Description	?? entityInfo.Description	?? $"{objectName} in the {serviceName} service";
			if (description.IsStartsWith("{{") && description.IsEndsWith("}}"))
				description = localization?.Get<string>(description.Replace("{{", "").Replace("}}", ""));

			var schema = new JObject
			{
				["$id"] = $"vieapps://schemas/{serviceName.ToLower()}/{objectName.ToLower()}/search-output",
				["title"] = $"Search {title} result",
				["description"] = $"Search result of {description}",
				["type"] = "object",
				["properties"] = new JObject
				{
					["Objects"] = new JObject
					{
						["type"] = "array",
						["items"] = outputSchema
					},
					["nextCursor"] = new JObject
					{
						["type"] = new JArray("string", "null"),
						["description"] = "Opaque cursor for continuing the search, or null when no more objects are available."
					}
				},
				["required"] = new JArray("Objects"),
				["additionalProperties"] = false
			};

			onCompleted?.Invoke(schema);
			return schema;
		}

		/// <summary>
		/// Generates all JSON schemas of this type for working with MCP tools
		/// </summary>
		/// <param name="type"></param>
		/// <param name="localization"></param>
		/// <param name="onCompleted"></param>
		/// <returns></returns>
		public static JObject GenerateToolsJsonSchema(this Type type, ExpandoObject localization = null, Action<JObject> onCompleted = null)
		{
			var entityDefinition = type.GetEntityDefinition() ?? throw new InformationInvalidException("The type is invalid (no entity definition)");

			var resourceInfo = type.GetCustomAttribute<McpResourceAttribute>();
			if (resourceInfo != null && resourceInfo.Ignore)
				return null;

			var inputSchema = type.GenerateInputJsonSchema(localization);
			var outputSchema = type.GenerateOutputJsonSchema(localization);
			var searchingInputSchema = type.GenerateSearchingInputJsonSchema(localization);
			var searchingOutputSchema = type.GenerateSearchingOutputJsonSchema(localization);

			if (inputSchema == null && outputSchema == null && searchingInputSchema == null && searchingOutputSchema == null)
				return null;

			var serviceName = entityDefinition.GetServiceName();
			var objectName = entityDefinition.GetObjectName(false);

			var entityInfo = type.GetCustomAttribute<EntityAttribute>();
			var title = resourceInfo?.Title ?? entityInfo?.Title ?? objectName;
			if (title.IsStartsWith("{{") && title.IsEndsWith("}}"))
				title = localization?.Get<string>(title.Replace("{{", "").Replace("}}", ""));

			var description = resourceInfo?.Description ?? entityInfo?.Description ?? $"{objectName} in the {serviceName} service";
			if (description.IsStartsWith("{{") && description.IsEndsWith("}}"))
				description = localization?.Get<string>(description.Replace("{{", "").Replace("}}", ""));

			var idSchema = new JObject
			{
				["type"] = "object",
				["properties"] = new JObject
				{
					["ID"] = new JObject
					{
						["type"] = "string",
						["minLength"] = 1,
						["maxLength"] = 32,
						["description"] = $"Identity of the {title}."
					}
				},
				["required"] = new JArray("ID"),
				["additionalProperties"] = false
			};

			var schema = new JObject
			{
				["service"] = serviceName,
				["object"] = objectName,
				["title"] = title,
				["description"] = description,
				["schemas"] = new JObject()
			};

			var schemas = schema.Get<JObject>("schemas");

			var excludedTools = resourceInfo?.ExcludedTools ?? "";

			if (!excludedTools.IsContains("Create") && inputSchema != null && outputSchema != null)
				schemas["create"] = new JObject
				{
					["input"] = inputSchema,
					["output"] = outputSchema.DeepClone()
				};

			if (!excludedTools.IsContains("Update") && inputSchema != null && outputSchema != null)
			{
				var updateInputSchema = inputSchema.DeepClone() as JObject;
				if (updateInputSchema != null)
				{
					var properties = updateInputSchema.Get<JObject>("properties") ?? new JObject();
					if (properties["ID"] == null)
						properties.AddFirst(new JProperty("ID", idSchema["properties"]?["ID"]?.DeepClone()));

					updateInputSchema["properties"] = properties;

					var required = updateInputSchema.Get<JArray>("required") ?? new JArray();
					if (!required.Any(item => item?.ToString().IsEquals("ID") == true))
						required.Insert(0, "ID");

					updateInputSchema["required"] = required;
				}

				schemas["update"] = new JObject
				{
					["input"] = updateInputSchema,
					["output"] = outputSchema.DeepClone()
				};
			}

			if (!excludedTools.IsContains("Delete"))
				schemas["delete"] = new JObject
				{
					["input"] = idSchema.DeepClone(),
					["output"] = outputSchema?.DeepClone()
				};

			if (!excludedTools.IsContains("Read") && outputSchema != null)
				schemas["read"] = new JObject
				{
					["input"] = idSchema.DeepClone(),
					["output"] = outputSchema.DeepClone()
				};

			if (!excludedTools.IsContains("Search") && searchingInputSchema != null && searchingOutputSchema != null)
				schemas["search"] = new JObject
				{
					["input"] = searchingInputSchema,
					["output"] = searchingOutputSchema
				};

			onCompleted?.Invoke(schema);
			return schema;
		}

		/// <summary>
		/// Generates details info of this type for working with MCP resources
		/// </summary>
		/// <param name="type"></param>
		/// <param name="localization"></param>
		/// <param name="onCompleted"></param>
		/// <returns></returns>
		/// <exception cref="InformationInvalidException"></exception>
		public static JObject GenerateResourceJsonSchema(this Type type, ExpandoObject localization = null, Action<JObject> onCompleted = null)
		{
			var entityDefinition = type.GetEntityDefinition() ?? throw new InformationInvalidException("The type is invalid (no entity definition)");

			var resourceInfo = type.GetCustomAttribute<McpResourceAttribute>();
			if (resourceInfo?.Ignore == true)
				return null;

			var serviceName = entityDefinition.GetServiceName();
			var objectName = entityDefinition.GetObjectName(false);

			var entityInfo = type.GetCustomAttribute<EntityAttribute>();

			var title = resourceInfo?.Title ?? entityInfo?.Title ?? objectName;
			if (title.IsStartsWith("{{") && title.IsEndsWith("}}"))
				title = localization?.Get<string>(title.Replace("{{", "").Replace("}}", ""));

			var description = resourceInfo?.Description ?? entityInfo?.Description ?? $"{objectName} in the {serviceName} service";
			if (description.IsStartsWith("{{") && description.IsEndsWith("}}"))
				description = localization?.Get<string>(description.Replace("{{", "").Replace("}}", ""));

			var resource = new JObject
			{
				["uriTemplate"] = $"{serviceName.ToLower()}://{objectName.ToLower()}/{{id}}",
				["name"] = $"{serviceName.ToLower()}.{objectName.ToLower()}",
				["title"] = title,
				["description"] = description,
				["mimeType"] = "application/json"
			};

			onCompleted?.Invoke(resource);
			return resource;
		}

		/// <summary>
		/// Sends MCP settings to channel of API Gateway
		/// </summary>
		/// <param name="mcpSettings"></param>
		public static void SendSettings(this Settings mcpSettings)
			=> new CommunicateMessage("apigateway")
			{
				Type = "MCP#UpdateSettings",
				Data = mcpSettings.ToJson()
			}.Send();

		/// <summary>
		/// Sends tool changed notification to subscribed MCP clients
		/// </summary>
		/// <param name="object"></param>
		public static void SendToolChangedNotification(this RepositoryBase @object)
			=> new CommunicateMessage("mcp")
			{
				Type = "tools/changed",
			}.Send();

		/// <summary>
		/// Sends resource created notification to subscribed MCP clients
		/// </summary>
		/// <param name="object"></param>
		public static void SendResourceCreatedNotification(this RepositoryBase @object)
			=> new CommunicateMessage("mcp")
			{
				Type = "resources/created",
			}.Send();

		/// <summary>
		/// Sends resource updated notification to subscribed MCP clients
		/// </summary>
		/// <param name="object"></param>
		/// <param name="objectName"></param>
		public static void SendResourceUpdatedNotification(this RepositoryBase @object, string objectName = null)
			=> new CommunicateMessage("mcp")
			{
				Type = "resources/updated",
				Data = new JObject { ["URI"] = $"{@object?.ServiceName?.ToLower()}://{(objectName ?? @object?.ObjectName)?.ToLower()}/{@object?.ID}" }
			}.Send();
	}

	/// <summary>
	/// Presents settings of a service for processing in a MCP server
	/// </summary>
	public class Settings
	{
		/// <summary>
		/// Creates new instance of settings for working with MCP clients
		/// </summary>
		public Settings() { }

		/// <summary>
		/// Gets or Sets the system identifier that the settings related to
		/// </summary>
		public virtual string SystemID { get; set; }

		/// <summary>
		/// Gets or Sets the state to allow anonymous
		/// </summary>
		public virtual bool AllowAnonymous { get; set; } = false;

		/// <summary>
		/// Gets or Sets the collection of available resources
		/// </summary>
		public virtual ConcurrentDictionary<string, JObject> Resources { get; set; } = new ConcurrentDictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Gets or Sets the collection of available tools
		/// </summary>
		public virtual ConcurrentDictionary<string, JObject> Tools { get; set; } = new ConcurrentDictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Normalizes
		/// </summary>
		/// <returns></returns>
		public virtual Settings Normalize()
		{
			this.Resources = this.Resources == null || this.Resources.IsEmpty ? null : this.Resources;
			this.Tools = this.Resources == null || this.Tools == null || this.Tools.IsEmpty ? null : this.Tools;
			return this.Resources == null && this.Tools == null ? null : this;
		}
	}

	public abstract class McpAttribute : Attribute
	{
		/// <summary>
		/// Gets or Sets the title
		/// </summary>
		public string Title { get; set; }

		/// <summary>
		/// Gets or Sets the description
		/// </summary>
		public string Description { get; set; }

		/// <summary>
		/// Gets or Sets the state to ignore
		/// </summary>
		public bool Ignore { get; set; } = false;
	}

	[AttributeUsage(AttributeTargets.Class)]
	public sealed class McpResourceAttribute : McpAttribute
	{
		/// <summary>
		/// Gets or Sets the excluded tools (create/read/update/delete/search)
		/// </summary>
		public string ExcludedTools { get; set; }
	}

	[AttributeUsage(AttributeTargets.Property)]
	public sealed class McpResourceJsonSchemaAttribute : McpAttribute
	{
		/// <summary>
		/// Gets or Sets the state to ignore this property in input JSON schema
		/// </summary>
		public bool IgnoreInput { get; set; }

		/// <summary>
		/// Gets or Sets the state to ignore this property in output JSON schema
		/// </summary>
		public bool IgnoreOutput { get; set; }

		/// <summary>
		/// Gets or Sets the state to ignore this property in search query of JSON schema
		/// </summary>
		public bool IgnoreSearch { get; set; }

		/// <summary>
		/// Gets or Sets the state to ignore this property in sort query of JSON schema
		/// </summary>
		public bool IgnoreSort { get; set; }

		/// <summary>
		/// Gets or Sets the enum values - seperated by semi-colon (;)
		/// </summary>
		public string EnumValues { get; set; }
	}

	public class MalformedRequestException : AppException
	{
		public MalformedRequestException() : base("Parse error: malformed JSON-RPC") { }
		public MalformedRequestException(string message) : base(message) { }
		public MalformedRequestException(Exception innerException) : base("Parse error: malformed JSON-RPC", innerException) { }
		public MalformedRequestException(string message, Exception innerException) : base(message, innerException) { }
	}

	public class InvalidRequestException : AppException
	{
		public InvalidRequestException() : base("Invalid JSON-RPC request") { }
		public InvalidRequestException(string message) : base(message) { }
		public InvalidRequestException(string message, Exception innerException) : base(message, innerException) { }
	}

	public class InvalidHeaderException : AppException
	{
		public InvalidHeaderException() : base("Invalid MCP request header") { }
		public InvalidHeaderException(string message) : base(message) { }
		public InvalidHeaderException(Exception innerException) : base("Invalid MCP request header", innerException) { }
		public InvalidHeaderException(string message, Exception innerException) : base(message, innerException) { }
	}

	public class InvalidBodyException : AppException
	{
		public InvalidBodyException() : base("Invalid MCP request body") { }
		public InvalidBodyException(string message) : base(message) { }
		public InvalidBodyException(Exception innerException) : base("Invalid MCP request body", innerException) { }
		public InvalidBodyException(string message, Exception innerException) : base(message, innerException) { }
	}

	public class InvalidProtocolException : AppException
	{
		public InvalidProtocolException() : base("Unsupported or invalid MCP protocol version") { }
		public InvalidProtocolException(string message) : base(message) { }
		public InvalidProtocolException(string message, Exception innerException) : base(message, innerException) { }
	}

	public class InvalidMethodException : AppException
	{
		public InvalidMethodException() : base("Invalid MCP method") { }
		public InvalidMethodException(string message) : base(message) { }
		public InvalidMethodException(Exception innerException) : base("Invalid MCP method", innerException) { }
		public InvalidMethodException(string message, Exception innerException) : base(message, innerException) { }
	}

	public class InvalidNameException : AppException
	{
		public InvalidNameException() : base("Invalid MCP name") { }
		public InvalidNameException(string message) : base(message) { }
		public InvalidNameException(Exception innerException) : base("Invalid MCP name", innerException) { }
		public InvalidNameException(string message, Exception innerException) : base(message, innerException) { }
	}

	public class InvalidParamsException : AppException
	{
		public InvalidParamsException() : base("Invalid MCP params") { }
		public InvalidParamsException(string message) : base(message) { }
		public InvalidParamsException(Exception innerException) : base("Invalid MCP params", innerException) { }
		public InvalidParamsException(string message, Exception innerException) : base(message, innerException) { }
	}

	public class InvalidCursorException : AppException
	{
		public InvalidCursorException() : base("Invalid MCP cursor") { }
		public InvalidCursorException(string message) : base(message) { }
		public InvalidCursorException(Exception innerException) : base("Invalid MCP cursor", innerException) { }
		public InvalidCursorException(string message, Exception innerException) : base(message, innerException) { }
	}

}