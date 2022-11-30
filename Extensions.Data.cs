#region Related components
using System;
using System.Linq;
using System.Dynamic;
using System.Globalization;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services
{
	public static partial class Extensions
	{

		#region Evaluate an formula
		/// <summary>
		/// Evaluates an Formula expression
		/// </summary>
		/// <param name="formula">The string that presents the formula</param>
		/// <param name="object">The current object (that bound to 'this' parameter when formula is an Javascript expression)</param>
		/// <param name="requestInfo">The requesting information</param>
		/// <param name="params">The additional parameters</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates (for evaluating an Javascript expression)</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types (for evaluating an Javascript expression)</param>
		/// <returns></returns>
		public static object Evaluate(this string formula, ExpandoObject @object, ExpandoObject requestInfo = null, ExpandoObject @params = null, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
		{
			// check
			formula = formula?.Trim();
			if (string.IsNullOrWhiteSpace(formula) || !formula.StartsWith("@"))
				throw new InformationInvalidException($"The formula expression [{formula}] is invalid (the formula expression must started by the '@' character)");

			var isJsExpression = formula.StartsWith("@[") && formula.EndsWith("]");
			var position = isJsExpression ? -1 : formula.IndexOf("(");
			if (position > 0 && !formula.EndsWith(")"))
				throw new InformationInvalidException($"The formula expression [{formula}] is invalid (the open and close tokens are required when the formula got a parameter, ex: @request.Body(ContentType.ID) - just like an Javascript function)");

			// prepare
			object value = null;
			var name = formula;
			if (position > 0)
			{
				name = formula.Left(position).Trim();
				formula = formula.Substring(position + 1, formula.Length - position - 2).Trim();
				formula = string.IsNullOrWhiteSpace(formula) || formula.Equals("@") ? "@now" : formula;
			}

			// value of an JavaScript expression
			if (isJsExpression || name.IsEquals("@script") || name.IsEquals("@javascript") || name.IsEquals("@js"))
				value = formula.JsEvaluate(@object, requestInfo, @params, embedObjects, embedTypes);

			// value of current object
			else if (name.IsEquals("@current") || name.IsEquals("@object"))
				value = formula.StartsWith("@")
					? formula.Evaluate(@object, requestInfo, @params, embedObjects, embedTypes)
					: @object?.Get(formula);

			// value of request information
			else if (name.IsStartsWith("@request"))
				value = formula.StartsWith("@")
					? formula.Evaluate(@object, requestInfo, @params, embedObjects, embedTypes)
					: name.IsEquals("@request.Session")
						? requestInfo?.Get<ExpandoObject>("Session")?.Get(formula)
						: name.IsEquals("@request.Query")
							? requestInfo?.Get<ExpandoObject>("Query")?.Get(formula)
							: name.IsEquals("@request.Header")
								? requestInfo?.Get<ExpandoObject>("Header")?.Get(formula)
								: name.IsEquals("@request.Extra")
									? requestInfo?.Get<ExpandoObject>("Extra")?.Get(formula)
									: name.IsEquals("@request.Body")
										? (requestInfo?.Get("Body") is ExpandoObject bodyAsExpando ? bodyAsExpando : requestInfo?.Get("Body") is string bodyAsString ? bodyAsString?.ToExpandoObject() : null)?.Get(formula)
										: requestInfo?.Get(formula);

			// value of parameters
			else if (name.IsEquals("@params") || name.IsEquals("@global"))
				value = formula.StartsWith("@")
					? formula.Evaluate(@object, requestInfo, @params, embedObjects, embedTypes)
					: @params?.Get(formula);

			// current date-time
			else if (name.IsEquals("@now") || name.IsEquals("@datetime.Now") || name.IsEquals("@date.Now") || name.IsEquals("@time.Now"))
				value = name.IsEquals("@date.Now")
					? DateTime.Parse($"{DateTime.Now:yyyy/MM/dd} 00:00:00")
					: DateTime.Now;

			// current date-time (as string)
			else if (name.IsEquals("@today") || name.IsStartsWith("@todayStr") || name.IsEquals("@datetime.Today") || name.IsEquals("@date.Today") || name.IsEquals("@time.Today") || name.IsStartsWith("@nowStr") || name.IsStartsWith("@datetime.NowStr") || name.IsStartsWith("@date.NowStr") || name.IsStartsWith("@time.NowStr"))
				value = DateTime.Now.ToDTString(false, name.IsStartsWith("@nowStr") || name.IsStartsWith("@datetime.NowStr") || name.IsStartsWith("@date.NowStr") || name.IsStartsWith("@time.NowStr"));

			// import static text/html/json from a remote end-point
			else if ((name.IsEquals("@import") || name.IsEquals("@static")) && (formula.IsStartsWith("https://") || formula.IsStartsWith("http://")))
				try
				{
					string url = formula, element = null;
					position = url.IndexOf(",");
					if (position > 0)
					{
						element = url.Right(url.Length - position - 1).Trim();
						url = url.Left(position).Trim();
					}
					var fetch = new Uri(url).FetchHttpAsync(null, 5);
					fetch.Wait(5000);
					value = string.IsNullOrWhiteSpace(element)
						? fetch.Result
						: fetch.Result?.ToExpandoObject()?.Get(element)?.ToString();
				}
				catch (Exception ex)
				{
					value = $"Error [{name}({formula})] => {ex.Message}";
				}

			// generate UUID
			else if (name.IsEquals("@uuid") || name.IsEquals("@generateid") || name.IsEquals("@generateuuid"))
			{
				var mode = "md5";
				position = formula.IndexOf(",");
				if (position > 0)
				{
					mode = formula.Right(formula.Length - position - 1).Trim();
					formula = formula.Left(position).Trim();
					formula = string.IsNullOrWhiteSpace(formula) || formula.Equals("@") ? "@now" : formula;
				}
				value = (formula.StartsWith("@") ? formula.Evaluate(@object, requestInfo, @params, embedObjects, embedTypes)?.ToString() ?? "" : formula).GenerateUUID(null, mode);
			}

			// pre-defined formulas
			else if (formula.StartsWith("@"))
			{
				// convert the value to floating point number
				if (name.IsStartsWith("@toDec") || name.IsStartsWith("@toNum") || name.IsStartsWith("@toFloat") || name.IsStartsWith("@toDouble"))
				{
					value = formula.Evaluate(@object, requestInfo, @params, embedObjects, embedTypes);
					value = value is DateTime datetime
						? datetime.ToUnixTimestamp().As<decimal>()
						: value != null && value.IsNumericType()
							? value.As<decimal>()
							: value;
				}

				// convert the value to integral number
				else if (name.IsStartsWith("@toInt") || name.IsStartsWith("@toLong") || name.IsStartsWith("@toByte") || name.IsStartsWith("@toShort"))
				{
					value = formula.Evaluate(@object, requestInfo, @params, embedObjects, embedTypes);
					value = value is DateTime datetime
						? datetime.ToUnixTimestamp()
						: value != null && value.IsNumericType()
							? value.As<long>()
							: value;
				}

				// convert the value to string
				else if (name.IsStartsWith("@toStr") || name.IsStartsWith("@date.toStr") || name.IsStartsWith("@time.toStr"))
				{
					var cultureInfoName = "";
					var format = formula.IsStartsWith("@date")
						? "dd/MM/yyyy HH:mm:ss"
						: formula.IsStartsWith("@time") ? "hh:mm tt @ dd/MM/yyyy" : "";
					position = formula.IndexOf(",");
					if (position > 0)
					{
						format = formula.Right(formula.Length - position - 1).Trim();
						formula = formula.Left(position).Trim();
						formula = string.IsNullOrWhiteSpace(formula) || formula.Equals("@") ? "@now" : formula;
						position = format.IndexOf(",");
						if (position > 0)
						{
							cultureInfoName = format.Right(format.Length - position - 1).Trim();
							format = format.Left(position).Trim();
						}
					}
					value = formula.Evaluate(@object, requestInfo, @params, embedObjects, embedTypes);
					value = value == null || string.IsNullOrWhiteSpace(format)
						? value?.ToString()
						: value.IsDateTimeType()
							? string.IsNullOrWhiteSpace(cultureInfoName) ? value.As<DateTime>().ToString(format) : value.As<DateTime>().ToString(format, CultureInfo.GetCultureInfo(cultureInfoName))
							: value.IsFloatingPointType()
								? string.IsNullOrWhiteSpace(cultureInfoName) ? value.As<decimal>().ToString(format) : value.As<decimal>().ToString(format, CultureInfo.GetCultureInfo(cultureInfoName))
								: value.IsIntegralType()
									? string.IsNullOrWhiteSpace(cultureInfoName) ? value.As<long>().ToString(format) : value.As<long>().ToString(format, CultureInfo.GetCultureInfo(cultureInfoName))
									: value.ToString();
				}

				// convert the value to lower-case string
				else if (name.IsStartsWith("@toLower"))
					value = formula.Evaluate(@object, requestInfo, @params, embedObjects, embedTypes)?.ToString().ToLower();

				// convert the value to upper-case string
				else if (name.IsStartsWith("@toUpper"))
					value = formula.Evaluate(@object, requestInfo, @params, embedObjects, embedTypes)?.ToString().ToUpper();

				// unknown => return the original formula
				else
					value = $"{name}{(string.IsNullOrWhiteSpace(formula) ? "" : $"({formula})")}";
			}

			// unknown => return the original formula
			else
				value = $"{name}{(string.IsNullOrWhiteSpace(formula) ? "" : $"({formula})")}";

			return value;
		}

		/// <summary>
		/// Evaluates an Formula expression
		/// </summary>
		/// <param name="formula">The string that presents the formula</param>
		/// <param name="object">The object for fetching data from</param>
		/// <param name="requestInfo">The object that presents the information of the request information</param>
		/// <param name="params">The additional parameters for fetching data from</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates (for evaluating an Javascript expression)</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types (for evaluating an Javascript expression)</param>
		/// <returns></returns>
		public static object Evaluate(this string formula, object @object = null, RequestInfo requestInfo = null, ExpandoObject @params = null, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
			=> formula?.Evaluate(@object is IBusinessEntity bizObject ? bizObject.ToExpandoObject() : @object?.ToExpandoObject(), requestInfo?.AsExpandoObject, @params, embedObjects, embedTypes);
		#endregion

		#region Filter
		/// <summary>
		/// Prepares the comparing values of the filtering expression (means evaluating all Formula/Javascript expressions)
		/// </summary>
		/// <param name="filterBy">The filtering expression</param>
		/// <param name="object">The object for fetching data from</param>
		/// <param name="requestInfo">The object that presents the information of the request information</param>
		/// <param name="params">The additional parameters for fetching data from</param>
		/// <param name="onCompleted">The action to run when the preparing process is completed</param>
		/// <returns>The filtering expression with all formula/expression values had been evaluated</returns>
		public static IFilterBy Prepare(this IFilterBy filterBy, ExpandoObject @object, ExpandoObject requestInfo = null, ExpandoObject @params = null, Action<IFilterBy> onCompleted = null)
		{
			// prepare value of a single filter (that presented by an JavaScript/Formulla expression)
			if (filterBy is FilterBy filter)
			{
				if (filter?.Value != null && filter.Value is string value && value.StartsWith("@"))
					filter.Value = value.Evaluate(@object, requestInfo, @params);
			}

			// prepare a group of filters
			else
				(filterBy as FilterBys)?.Children?.ForEach(filterby => filterby?.Prepare(@object, requestInfo, @params, onCompleted));

			// complete
			onCompleted?.Invoke(filterBy);
			return filterBy;
		}

		/// <summary>
		/// Prepares the comparing values of the filtering expression (means evaluating all Formula/Javascript expressions)
		/// </summary>
		/// <param name="filterBy">The filtering expression</param>
		/// <param name="object">The object for fetching data from</param>
		/// <param name="requestInfo">The object that presents the information of the request information</param>
		/// <param name="params">The additional parameters for fetching data from</param>
		/// <param name="onCompleted">The action to run when the preparing process is completed</param>
		/// <returns>The filtering expression with all formula/expression values had been evaluated</returns>
		public static IFilterBy Prepare(this IFilterBy filterBy, object @object = null, RequestInfo requestInfo = null, ExpandoObject @params = null, Action<IFilterBy> onCompleted = null)
			=> filterBy?.Prepare(@object is IBusinessEntity bizObject ? bizObject.ToExpandoObject() : @object?.ToExpandoObject(), requestInfo?.AsExpandoObject, @params, onCompleted);

		/// <summary>
		/// Prepares the comparing values of the filtering expression (means evaluating all Formula/Javascript expressions)
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filterBy">The filtering expression</param>
		/// <param name="object">The object for fetching data from</param>
		/// <param name="requestInfo">The object that presents the information of the request information</param>
		/// <param name="params">The additional parameters for fetching data from</param>
		/// <param name="onCompleted">The action to run when the preparing process is completed</param>
		/// <returns>The filtering expression with all formula/expression values had been evaluated</returns>
		public static IFilterBy<T> Prepare<T>(this IFilterBy<T> filterBy, object @object = null, RequestInfo requestInfo = null, ExpandoObject @params = null, Action<IFilterBy<T>> onCompleted = null) where T : class
			=> (filterBy as IFilterBy)?.Prepare(@object, requestInfo, @params, onCompleted as Action<IFilterBy>) as IFilterBy<T>;

		/// <summary>
		/// Prepares the comparing values of the filtering expression (means evaluating all Formula/Javascript expressions)
		/// </summary>
		/// <param name="filterBy">The filtering expression</param>
		/// <param name="requestInfo">The object that presents the information of the request information</param>
		/// <param name="onCompleted">The action to run when the preparing process is completed</param>
		/// <returns>The filtering expression with all formula/expression values had been evaluated</returns>
		public static IFilterBy Prepare(this IFilterBy filterBy, RequestInfo requestInfo, Action<IFilterBy> onCompleted = null)
		{
			filterBy?.Prepare(null, requestInfo, null);
			onCompleted?.Invoke(filterBy);
			return filterBy;
		}

		/// <summary>
		/// Prepares the comparing values of the filtering expression (means evaluating all Formula/Javascript expressions)
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filterBy">The filtering expression</param>
		/// <param name="requestInfo">The object that presents the information of the request information</param>
		/// <param name="onCompleted">The action to run when the preparing process is completed</param>
		/// <returns>The filtering expression with all formula/expression values had been evaluated</returns>
		public static IFilterBy<T> Prepare<T>(this IFilterBy<T> filterBy, RequestInfo requestInfo, Action<IFilterBy<T>> onCompleted = null) where T : class
		{
			filterBy?.Prepare(null, requestInfo, null);
			onCompleted?.Invoke(filterBy);
			return filterBy;
		}

		/// <summary>
		/// Gets a child expression (comparision expression) by the specified name
		/// </summary>
		/// <param name="filter"></param>
		/// <param name="name">The name of a child expression</param>
		/// <returns></returns>
		public static IFilterBy GetChild(this IFilterBy filter, string name)
			=> filter != null && filter is FilterBys && !string.IsNullOrWhiteSpace(name)
				? (filter as FilterBys).Children?.FirstOrDefault(filterby => filterby is FilterBy filterBy && name.IsEquals(filterBy.Attribute))
				: null;

		/// <summary>
		/// Gets a child expression (comparision expression) by the specified name
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter"></param>
		/// <param name="name">The name of a child expression</param>
		/// <returns></returns>
		public static IFilterBy<T> GetChild<T>(this IFilterBy<T> filter, string name) where T : class
			=> filter != null && filter is FilterBys<T> && !string.IsNullOrWhiteSpace(name)
				? (filter as IFilterBy).GetChild(name) as IFilterBy<T>
				: null;

		/// <summary>
		/// Gets the value of a child expression (comparision expression) by the specified name
		/// </summary>
		/// <param name="filter"></param>
		/// <param name="name">The name of a child expression</param>
		/// <returns></returns>
		public static string GetValue(this IFilterBy filter, string name)
			=> filter != null && !string.IsNullOrWhiteSpace(name)
				? (filter.GetChild(name) as FilterBy)?.Value as string
				: null;

		/// <summary>
		/// Gets the value of a child expression (comparision expression) by the specified name
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter"></param>
		/// <param name="name">The name of a child expression</param>
		/// <returns></returns>
		public static string GetValue<T>(this IFilterBy<T> filter, string name) where T : class
			=> filter != null && !string.IsNullOrWhiteSpace(name)
				? (filter.GetChild(name) as FilterBy<T>)?.Value as string
				: null;

		static IFilterBy<T> GetFilterBy<T>(this JObject expression) where T : class
		{
			var property = expression.Properties()?.FirstOrDefault();
			if (property == null || property.Value == null)
				return null;

			IFilterBy<T> filter = null;
			var attribute = property.Name;

			// group of comparisions
			if (attribute.IsEquals("And") || attribute.IsEquals("Or"))
			{
				filter = attribute.IsEquals("Or") ? Filters<T>.Or() : Filters<T>.And();
				(property.Value is JObject pobj ? pobj.ToJArray(kvp => new JObject { { kvp.Key, kvp.Value } }) : property.Value).ForEach(exp => (filter as FilterBys<T>).Add(exp != null && exp is JObject eobj ? eobj.GetFilterBy<T>() : null));
				if (!(filter as FilterBys<T>).Children.Any())
					filter = null;
			}

			// single comparision
			else
			{
				var @operator = "";
				var value = JValue.CreateNull();

				// special comparison
				if (property.Value is JValue pvalue)
				{
					@operator = pvalue.Value.ToString();
					if (!@operator.IsEquals("IsNull") && !@operator.IsEquals("IsNotNull") && !@operator.IsEquals("IsEmpty") && !@operator.IsEquals("IsNotEmpty"))
						@operator = null;
				}

				// normal comparison
				else if (property.Value is JObject pobj)
				{
					property = pobj.Properties()?.FirstOrDefault();
					if (property != null && property.Value != null && property.Value is JValue jvalue && jvalue.Value != null)
					{
						@operator = property.Name;
						value = jvalue;
					}
					else
						@operator = null;
				}

				// unknown comparison
				else
					@operator = null;

				filter = @operator != null
					? new FilterBy<T>(new JObject
					{
						{ "Attribute", attribute },
						{ "Operator", @operator },
						{ "Value", value }
					})
					: null;
			}

			return filter;
		}

		/// <summary>
		/// Converts the (client) JSON object to a filtering expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="expression"></param>
		/// <returns></returns>
		public static IFilterBy<T> ToFilterBy<T>(this JObject expression) where T : class
		{
			var property = expression.Properties()?.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Name) && !p.Name.IsEquals("Query"));
			if (property == null || property.Value == null)
				return null;

			var filter = property.Name.IsEquals("Or") ? Filters<T>.Or() : Filters<T>.And();
			if (!property.Name.IsEquals("And") && !property.Name.IsEquals("Or"))
				expression.ToJArray(kvp => new JObject
				{
					{ kvp.Key, kvp.Value }
				}).ForEach(exp => filter.Add((exp as JObject).GetFilterBy<T>()));
			else
			{
				var children = property.Name.IsEquals("Or") ? expression["Or"] : expression["And"];
				(children is JObject cobj ? cobj.ToJArray(kvp => new JObject
				{
					{ kvp.Key, kvp.Value }
				}) : children as JArray).ForEach(exp => filter.Add(exp != null && exp is JObject eobj ? eobj.GetFilterBy<T>() : null));
			}

			return filter != null && filter.Children.Any() ? filter : null;
		}

		/// <summary>
		/// Converts the (client) ExpandoObject object to a filtering expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="expression"></param>
		/// <returns></returns>
		public static IFilterBy<T> ToFilterBy<T>(this ExpandoObject expression) where T : class
			=> expression != null ? JObject.FromObject(expression).ToFilterBy<T>() : null;

		/// <summary>
		/// Converts the (server) JSON object to a filtering expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="expression"></param>
		/// <returns></returns>
		public static IFilterBy<T> ToFilter<T>(this JObject expression) where T : class
		{
			var @operator = expression?.Get<string>("Operator");
			return @operator != null
				? @operator.IsEquals("Or") || @operator.IsEquals("And")
					? new FilterBys<T>(expression, @operator.IsEquals("Or") ? GroupOperator.Or : GroupOperator.And)
					: new FilterBy<T>(expression) as IFilterBy<T>
				: null;
		}

		/// <summary>
		/// Converts the (server) ExpandoObject object to a filtering expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="expression"></param>
		/// <returns></returns>
		public static IFilterBy<T> ToFilter<T>(this ExpandoObject expression) where T : class
			=> expression != null ? JObject.FromObject(expression).ToFilter<T>() : null;

		static JToken GetClientJson(this JToken serverJson, out string name)
		{
			var @operator = serverJson.Get<string>("Operator");
			var children = serverJson.Get<JArray>("Children");
			if (children == null)
			{
				@operator = @operator ?? CompareOperator.Equals.ToString();
				name = serverJson.Get<string>("Attribute");
				return @operator.IsEquals("IsNull") || @operator.IsEquals("IsNotNull") || @operator.IsEquals("IsEmpty") || @operator.IsEquals("IsNotEmpty")
					? new JValue(@operator) as JToken
					: new JObject { { @operator, serverJson["Value"] as JValue } };
			}
			else
			{
				@operator = @operator ?? GroupOperator.And.ToString();
				name = @operator;
				return children.ToJArray(json =>
				{
					var value = json.GetClientJson(out @operator);
					return new JObject
					{
						{ @operator, json.GetClientJson(out @operator) }
					};
				});
			}
		}

		/// <summary>
		/// Converts the filtering expression to JSON for using at client-side
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter"></param>
		/// <param name="query"></param>
		/// <returns></returns>
		public static JObject ToClientJson<T>(this IFilterBy<T> filter, string query = null) where T : class
		{
			var clientJson = new JObject();

			if (!string.IsNullOrWhiteSpace(query))
				clientJson["Query"] = query;

			var json = filter.ToJson().GetClientJson(out string @operator);
			clientJson[@operator] = json;

			return clientJson;
		}

		/// <summary>
		/// Generates the UUID of this filter expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter"></param>
		/// <returns></returns>
		public static string GenerateUUID<T>(this IFilterBy<T> filter) where T : class
			=> filter?.ToClientJson().ToString(Formatting.None).ToLower().GenerateUUID();
		#endregion

		#region Sort
		/// <summary>
		/// Converts the (client) JSON object to a sorting expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="expression"></param>
		/// <returns></returns>
		public static SortBy<T> ToSortBy<T>(this JObject expression) where T : class
		{
			SortBy<T> sort = null;
			expression?.ForEach(kvp =>
			{
				var attribute = kvp.Key;
				if (!((kvp.Value as JValue).Value?.ToString() ?? "Ascending").TryToEnum(out SortMode mode))
					mode = SortMode.Ascending;
				sort = sort != null
					? mode.Equals(SortMode.Ascending)
						? sort.ThenByAscending(attribute)
						: sort.ThenByDescending(attribute)
					: mode.Equals(SortMode.Ascending)
						? Sorts<T>.Ascending(attribute)
						: Sorts<T>.Descending(attribute);
			});
			return sort;
		}

		/// <summary>
		/// Converts the (client) ExpandoObject object to a sorting expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="expression"></param>
		/// <returns></returns>
		public static SortBy<T> ToSortBy<T>(this ExpandoObject expression) where T : class
			=> expression != null ? JObject.FromObject(expression).ToSortBy<T>() : null;

		/// <summary>
		/// Converts the (server) JSON object to a sorting expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="expression"></param>
		/// <returns></returns>
		public static SortBy<T> ToSort<T>(this JObject expression) where T : class
			=> !string.IsNullOrWhiteSpace(expression?.Get<string>("Attribute")) ? new SortBy<T>(expression) : null;

		/// <summary>
		/// Converts the (server) ExpandoObject object to a sorting expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="expression"></param>
		/// <returns></returns>
		public static SortBy<T> ToSort<T>(this ExpandoObject expression) where T : class
			=> expression != null ? JObject.FromObject(expression).ToSort<T>() : null;

		static void GetClientJson(this JToken serverJson, JObject clientJson)
		{
			var attribute = serverJson?.Get<string>("Attribute");
			if (!string.IsNullOrWhiteSpace(attribute))
				clientJson[attribute] = serverJson.Get("Mode", "Ascending");
			serverJson?.Get<JObject>("ThenBy")?.GetClientJson(clientJson);
		}

		/// <summary>
		/// Converts the sorting expression to JSON for using at client-side
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="sort"></param>
		/// <returns></returns>
		public static JObject ToClientJson<T>(this SortBy<T> sort) where T : class
		{
			JObject clientJson = null;
			if (sort != null)
			{
				clientJson = new JObject();
				sort.ToJson().GetClientJson(clientJson);
			}
			return clientJson;
		}

		/// <summary>
		/// Generates the UUID of this sort expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="sortby"></param>
		/// <returns></returns>
		public static string GenerateUUID<T>(this SortBy<T> sortby) where T : class
			=> sortby?.ToClientJson().ToString(Formatting.None).ToLower().GenerateUUID();
		#endregion

		#region Pagination
		/// <summary>
		/// Computes the total of pages from total of records and page size
		/// </summary>
		/// <param name="totalRecords"></param>
		/// <param name="pageSize"></param>
		/// <returns></returns>
		public static int GetTotalPages(long totalRecords, int pageSize)
		{
			var totalPages = pageSize > 0 ? (int)(totalRecords / pageSize) : 1;
			if (pageSize > 0 && totalRecords - (totalPages * pageSize) > 0)
				totalPages += 1;
			return totalPages;
		}

		/// <summary>
		/// Computes the total of pages from total of records and page size
		/// </summary>
		/// <param name="info"></param>
		/// <returns></returns>
		public static int GetTotalPages(this Tuple<long, int> info)
			=> Extensions.GetTotalPages(info.Item1, info.Item2);

		/// <summary>
		/// Gets the tuple of pagination from this JSON (1st element is total records, 2nd element is total pages, 3rd element is page size, 4th element is page number)
		/// </summary>
		/// <param name="pagination"></param>
		/// <returns></returns>
		public static Tuple<long, int, int, int> GetPagination(this JObject pagination)
		{
			var totalRecords = pagination["TotalRecords"] != null && pagination["TotalRecords"] is JValue totalRecordsAsJValue && totalRecordsAsJValue.Value != null
				? totalRecordsAsJValue.Value.CastAs<long>()
				: -1;

			var pageSize = pagination["PageSize"] != null && pagination["PageSize"] is JValue pageSizeAsJValue && pageSizeAsJValue.Value != null
				? pageSizeAsJValue.Value.CastAs<int>()
				: 20;
			if (pageSize < 0)
				pageSize = 20;

			var totalPages = pagination["TotalPages"] != null && pagination["TotalPages"] is JValue totalPagesAsJValue && totalPagesAsJValue.Value != null
				? totalPagesAsJValue.Value.CastAs<int>()
				: -1;
			if (totalPages < 0)
				totalPages = Extensions.GetTotalPages(totalRecords, pageSize);

			var pageNumber = pagination["PageNumber"] != null && pagination["PageNumber"] is JValue pageNumberAsJValue && pageNumberAsJValue.Value != null
				? pageNumberAsJValue.Value.CastAs<int>()
				: 20;
			if (pageNumber < 1)
				pageNumber = 1;
			else if (totalPages > 0 && pageNumber > totalPages)
				pageNumber = totalPages;

			return new Tuple<long, int, int, int>(totalRecords, totalPages, pageSize, pageNumber);
		}

		/// <summary>
		/// Gets the tuple of pagination from this object (1st element is total records, 2nd element is total pages, 3rd element is page size, 4th element is page number)
		/// </summary>
		/// <param name="pagination"></param>
		/// <returns></returns>
		public static Tuple<long, int, int, int> GetPagination(this ExpandoObject pagination)
		{
			var totalRecords = pagination.Get<long>("TotalRecords", -1);

			var pageSize = pagination.Get("PageSize", 20);
			pageSize = pageSize < 0 ? 10 : pageSize;

			var totalPages = pagination.Get("TotalPages", -1);
			totalPages = totalPages < 0
				? totalRecords > 0 ? Extensions.GetTotalPages(totalRecords, pageSize) : 0
				: totalPages;

			var pageNumber = pagination.Get("PageNumber", 1);
			pageNumber = pageNumber < 1
				? 1
				: totalPages > 0 && pageNumber > totalPages ? totalPages : pageNumber;

			return new Tuple<long, int, int, int>(totalRecords, totalPages, pageSize, pageNumber);
		}

		/// <summary>
		/// Gets the pagination JSON
		/// </summary>
		/// <param name="totalRecords"></param>
		/// <param name="totalPages"></param>
		/// <param name="pageSize"></param>
		/// <param name="pageNumber"></param>
		/// <returns></returns>
		public static JObject GetPagination(long totalRecords, int totalPages, int pageSize, int pageNumber)
			=> new JObject
			{
				{ "TotalRecords", totalRecords },
				{ "TotalPages", totalPages},
				{ "PageSize", pageSize },
				{ "PageNumber", pageNumber }
			};

		/// <summary>
		/// Gets the pagination JSON
		/// </summary>
		/// <param name="pagination"></param>
		/// <returns></returns>
		public static JObject GetPagination(this Tuple<long, int, int, int> pagination)
			=> Extensions.GetPagination(pagination.Item1, pagination.Item2, pagination.Item3, pagination.Item4);
		#endregion

		#region Cache keys
		/// <summary>
		/// Gets the caching key
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <returns></returns>
		public static string GetCacheKey<T>() where T : class
			=> typeof(T).GetTypeName(true);

		/// <summary>
		/// Gets the caching key
		/// </summary>
		/// <param name="prefix">The string that presents the prefix of the caching key</param>
		/// <param name="pageSize">The page size</param>
		/// <param name="pageNumber">The page number</param>
		/// <param name="addPageNumberHolder">true to add page number as a holder ({{pageNumber}})</param>
		/// <param name="suffix">The string that presents the suffix of the caching key</param>
		/// <returns></returns>
		public static string GetCacheKey(string prefix, int pageSize = 0, int pageNumber = 0, bool addPageNumberHolder = false, string suffix = null)
			=> $"{prefix}{(pageNumber > 0 ? $"#p:{(addPageNumberHolder ? "{{pageNumber}}" : $"{pageNumber}")}{(pageSize > 0 ? $"~{pageSize}" : "")}" : "")}{suffix ?? ""}";

		/// <summary>
		/// Gets the caching key
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="prefix">The string that presents the prefix of the caching key</param>
		/// <param name="pageSize">The page size</param>
		/// <param name="pageNumber">The page number</param>
		/// <param name="addPageNumberHolder">true to add page number as '[page-number]' holder</param>
		/// <param name="suffix">The string that presents the suffix of the caching key</param>
		/// <returns></returns>
		public static string GetCacheKey<T>(string prefix, int pageSize = 0, int pageNumber = 0, bool addPageNumberHolder = false, string suffix = null) where T : class
			=> $"{Extensions.GetCacheKey<T>()}{Extensions.GetCacheKey(prefix, pageSize, pageNumber, addPageNumberHolder, suffix)}";

		/// <summary>
		/// Gets the caching key
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter">The filter expression</param>
		/// <param name="sort">The sort expression</param>
		/// <param name="pageSize">The page size</param>
		/// <param name="pageNumber">The page number</param>
		/// <param name="addPageNumberHolder">true to add page number as '[page-number]' holder</param>
		/// <param name="suffix">The string that presents the suffix of the caching key</param>
		/// <returns></returns>
		public static string GetCacheKey<T>(IFilterBy<T> filter, SortBy<T> sort, int pageSize = 0, int pageNumber = 0, bool addPageNumberHolder = false, string suffix = null) where T : class
			=> Extensions.GetCacheKey<T>($"{(filter != null ? $"#f:{filter.GenerateUUID()}" : "")}{(sort != null ? $"#s:{sort.GenerateUUID()}" : "")}", pageSize, pageNumber, addPageNumberHolder, suffix);

		static List<string> KeyPatterns => "total,json,xml".ToList();

		static List<string> RelatedKeyPatterns => "thumbnails,attachments,others,newers,olders".ToList();

		/// <summary>
		/// Gets the related caching key for working with collection of objects
		/// </summary>
		/// <param name="key">The pre-buid key</param>
		/// <param name="pageSize">The size of one page</param>
		/// <returns>The collection presents all related caching keys (10 first pages)</returns>
		public static List<string> GetRelatedCacheKeys(string key, int pageSize = 0)
		{
			var singleKey = Extensions.GetCacheKey(key, 0, 1);
			var relatedKeys = new List<string> { key, singleKey };
			Extensions.KeyPatterns.Concat(Extensions.RelatedKeyPatterns).ForEach(pattern =>
			{
				relatedKeys.Add($"{key}:{pattern}");
				relatedKeys.Add($"{singleKey}:{pattern}");
			});
			var paginationKey = Extensions.GetCacheKey(key, pageSize > 0 ? pageSize : 20, 1, true);
			for (var pageNumber = 1; pageNumber <= 10; pageNumber++)
			{
				var pageKey = paginationKey.Replace(StringComparison.OrdinalIgnoreCase, "{{pageNumber}}", $"{pageNumber}");
				relatedKeys.Add(pageKey);
				Extensions.KeyPatterns.ForEach(pattern => relatedKeys.Add($"{pageKey}:{pattern}"));
			}
			return relatedKeys;
		}

		/// <summary>
		/// Gets the related caching key for working with collection of objects
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter">The filter expression</param>
		/// <param name="sort">The sort expression</param>
		/// <param name="pageSize">The size of one page</param>
		/// <returns>The collection presents all related caching keys (10 first pages)</returns>
		public static List<string> GetRelatedCacheKeys<T>(IFilterBy<T> filter, SortBy<T> sort, int pageSize = 0) where T : class
			=> Extensions.GetRelatedCacheKeys(Extensions.GetCacheKey<T>(filter, sort), pageSize);

		/// <summary>
		/// Gets the caching key for workingwith the number of total objects
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="prefix">The string that presents the prefix of the caching key</param>
		/// <param name="suffix">The string that presents the suffix of the caching key</param>
		/// <returns>The string that presents a caching key</returns>
		public static string GetCacheKeyOfTotalObjects<T>(string prefix, string suffix = null) where T : class
			=> $"{Extensions.GetCacheKey<T>(prefix)}:total{suffix ?? ""}";

		/// <summary>
		/// Gets the caching key for workingwith the number of total objects
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter">The filter expression</param>
		/// <param name="sort">The sort expression</param>
		/// <param name="suffix">The string that presents the suffix of the caching key</param>
		/// <returns>The string that presents a caching key</returns>
		public static string GetCacheKeyOfTotalObjects<T>(IFilterBy<T> filter, SortBy<T> sort, string suffix = null) where T : class
			=> Extensions.GetCacheKeyOfTotalObjects<T>((filter != null ? $"#f:{filter.GenerateUUID()}" : "") + (sort != null ? $"#s:{sort.GenerateUUID()}" : ""), suffix);

		/// <summary>
		/// Gets the caching key for working with the JSON of objects
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="prefix">The string that presents the prefix of the caching key</param>
		/// <param name="pageNumber">The page number</param>
		/// <param name="pageSize">The page size</param>
		/// <param name="suffix">The string that presents the suffix of the caching key</param>
		/// <returns>The string that presents a caching key</returns>
		public static string GetCacheKeyOfObjectsJson<T>(string prefix, int pageSize = 0, int pageNumber = 0, string suffix = null) where T : class
			=> $"{Extensions.GetCacheKey<T>(prefix, pageSize, pageNumber)}:json{suffix ?? ""}";

		/// <summary>
		/// Gets the caching key for working with the JSON of objects
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter">The filter expression</param>
		/// <param name="sort">The sort expression</param>
		/// <param name="pageNumber">The page number</param>
		/// <param name="pageSize">The page size</param>
		/// <param name="suffix">The string that presents the suffix of the caching key</param>
		/// <returns>The string that presents a caching key</returns>
		public static string GetCacheKeyOfObjectsJson<T>(IFilterBy<T> filter, SortBy<T> sort, int pageSize = 0, int pageNumber = 0, string suffix = null) where T : class
			=> Extensions.GetCacheKeyOfObjectsJson<T>((filter != null ? $"#f:{filter.GenerateUUID()}" : "") + (sort != null ? $"#s:{sort.GenerateUUID()}" : ""), pageSize, pageNumber, suffix);

		/// <summary>
		/// Gets the caching key for working with the XML of objects
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="prefix">The string that presents the prefix of the caching key</param>
		/// <param name="pageNumber">The page number</param>
		/// <param name="pageSize">The page size</param>
		/// <param name="suffix">The string that presents the suffix of the caching key</param>
		/// <returns>The string that presents a caching key</returns>
		public static string GetCacheKeyOfObjectsXml<T>(string prefix, int pageSize = 0, int pageNumber = 0, string suffix = null) where T : class
			=> $"{Extensions.GetCacheKey<T>(prefix, pageSize, pageNumber)}:xml{suffix ?? ""}";

		/// <summary>
		/// Gets the caching key for working with the XML of objects
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter">The filter expression</param>
		/// <param name="sort">The sort expression</param>
		/// <param name="pageNumber">The page number</param>
		/// <param name="pageSize">The page size</param>
		/// <param name="suffix">The string that presents the suffix of the caching key</param>
		/// <returns>The string that presents a caching key</returns>
		public static string GetCacheKeyOfObjectsXml<T>(IFilterBy<T> filter, SortBy<T> sort, int pageSize = 0, int pageNumber = 0, string suffix = null) where T : class
			=> Extensions.GetCacheKeyOfObjectsXml<T>((filter != null ? $"#f:{filter.GenerateUUID()}" : "") + (sort != null ? $"#s:{sort.GenerateUUID()}" : ""), pageSize, pageNumber, suffix);
		#endregion

		#region Double braces tokens & date-time quater
		/// <summary>
		/// Prepares the parameters of double braces (mustache-style - {{ }}) parameters
		/// </summary>
		/// <param name="doubleBracesTokens"></param>
		/// <param name="object"></param>
		/// <param name="requestInfo"></param>
		/// <param name="params"></param>
		/// <returns></returns>
		public static IDictionary<string, object> PrepareDoubleBracesParameters(this List<Tuple<string, string>> doubleBracesTokens, ExpandoObject @object, ExpandoObject requestInfo = null, ExpandoObject @params = null)
			=> doubleBracesTokens == null || !doubleBracesTokens.Any()
				? new Dictionary<string, object>()
				: doubleBracesTokens
					.Select(token => token.Item2)
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToDictionary(token => token, token => token.StartsWith("@") ? token.Evaluate(@object, requestInfo, @params) : token);

		/// <summary>
		/// Prepares the parameters of double braces (mustache-style - {{ }}) parameters
		/// </summary>
		/// <param name="doubleBracesTokens"></param>
		/// <param name="object"></param>
		/// <param name="requestInfo"></param>
		/// <param name="params"></param>
		/// <returns></returns>
		public static IDictionary<string, object> PrepareDoubleBracesParameters(this List<Tuple<string, string>> doubleBracesTokens, object @object = null, RequestInfo requestInfo = null, ExpandoObject @params = null)
			=> doubleBracesTokens?.PrepareDoubleBracesParameters(@object is IBusinessEntity bizObject ? bizObject.ToExpandoObject() : @object?.ToExpandoObject(), requestInfo?.AsExpandoObject, @params);

		/// <summary>
		/// Prepares the parameters of double braces (mustache-style - {{ }}) parameters
		/// </summary>
		/// <param name="string"></param>
		/// <param name="object"></param>
		/// <param name="requestInfo"></param>
		/// <param name="params"></param>
		/// <returns></returns>
		public static IDictionary<string, object> PrepareDoubleBracesParameters(this string @string, ExpandoObject @object, ExpandoObject requestInfo = null, ExpandoObject @params = null)
			=> string.IsNullOrWhiteSpace(@string)
				? new Dictionary<string, object>()
				: @string.GetDoubleBracesTokens().PrepareDoubleBracesParameters(@object, requestInfo, @params);

		/// <summary>
		/// Prepares the parameters of double braces (mustache-style - {{ }}) parameters
		/// </summary>
		/// <param name="string"></param>
		/// <param name="object"></param>
		/// <param name="requestInfo"></param>
		/// <param name="params"></param>
		/// <returns></returns>
		public static IDictionary<string, object> PrepareDoubleBracesParameters(this string @string, object @object = null, RequestInfo requestInfo = null, ExpandoObject @params = null)
			=> @string?.PrepareDoubleBracesParameters(@object is IBusinessEntity bizObject ? bizObject.ToExpandoObject() : @object?.ToExpandoObject(), requestInfo?.AsExpandoObject, @params);

		/// <summary>
		/// Gets the time quater
		/// </summary>
		/// <param name="time"></param>
		/// <param name="getHighValue"></param>
		/// <returns></returns>
		public static DateTime GetTimeQuarter(this DateTime time, bool getHighValue = true)
			=> time.Minute <= 15
				? getHighValue ? DateTime.Parse($"{time:yyyy/MM/dd HH}:15:00") : DateTime.Parse($"{time:yyyy/MM/dd HH}:00:00")
				: time.Minute <= 30
					? getHighValue ? DateTime.Parse($"{time:yyyy/MM/dd HH}:30:00") : DateTime.Parse($"{time:yyyy/MM/dd HH}:16:00")
					: time.Minute <= 45
						? getHighValue ? DateTime.Parse($"{time:yyyy/MM/dd HH}:45:00") : DateTime.Parse($"{time:yyyy/MM/dd HH}:31:00")
						: getHighValue ? DateTime.Parse($"{time:yyyy/MM/dd HH}:59:59") : DateTime.Parse($"{time:yyyy/MM/dd HH}:46:00");
		#endregion

		#region Version contents
		/// <summary>
		/// Gets service name of an entity definition
		/// </summary>
		/// <param name="definition"></param>
		/// <returns></returns>
		public static string GetServiceName(this EntityDefinition definition)
			=> definition?.RepositoryDefinition?.ServiceName;

		/// <summary>
		/// Gets service name of a service object
		/// </summary>
		/// <param name="object"></param>
		/// <returns></returns>
		public static string GetServiceName(this RepositoryBase @object)
			=> RepositoryMediator.GetEntityDefinition(@object?.GetType())?.GetServiceName();

		/// <summary>
		/// Gets name of a service object of an entity definition
		/// </summary>
		/// <param name="definition"></param>
		/// <param name="includePrefixAndSuffix"></param>
		/// <returns></returns>
		public static string GetObjectName(this EntityDefinition definition, bool includePrefixAndSuffix = true)
			=> includePrefixAndSuffix
				? $"{(string.IsNullOrWhiteSpace(definition?.ObjectNamePrefix) ? "" : definition?.ObjectNamePrefix)}{definition?.ObjectName}{(string.IsNullOrWhiteSpace(definition?.ObjectNameSuffix) ? "" : definition?.ObjectNameSuffix)}"
				: definition?.ObjectName;

		/// <summary>
		/// Gets name of a service object
		/// </summary>
		/// <param name="object"></param>
		/// <param name="includePrefixAndSuffix"></param>
		/// <returns></returns>
		public static string GetObjectName(this RepositoryBase @object, bool includePrefixAndSuffix = true)
			=> RepositoryMediator.GetEntityDefinition(@object?.GetType())?.GetObjectName(includePrefixAndSuffix);

		/// <summary>
		/// Finds version contents of an object
		/// </summary>
		/// <param name="object"></param>
		/// <param name="cancellationToken"></param>
		/// <param name="sendUpdateMessage"></param>
		/// <returns></returns>
		public static async Task<List<VersionContent>> FindVersionsAsync(this RepositoryBase @object, CancellationToken cancellationToken = default, bool sendUpdateMessage = true)
		{
			if (string.IsNullOrWhiteSpace(@object?.ID))
				return null;

			var definition = RepositoryMediator.GetEntityDefinition(@object.GetType());
			var versions = definition.Cache != null
				? await definition.Cache.GetAsync<List<VersionContent>>($"{@object.GetCacheKey()}:Versions", cancellationToken).ConfigureAwait(false)
				: null;

			if (versions == null)
			{
				versions = await RepositoryMediator.FindVersionContentsAsync(@object.ID, cancellationToken).ConfigureAwait(false);
				if (definition.Cache != null)
					await definition.Cache.SetAsync($"{@object.GetCacheKey()}:Versions", versions, 0, cancellationToken).ConfigureAwait(false);
			}

			if (sendUpdateMessage)
				new UpdateMessage
				{
					Type = $"{definition.GetServiceName()}#{definition.GetObjectName()}#Update",
					Data = new JObject
					{
						["ID"] = @object.ID,
						["SystemID"] = @object.SystemID,
						["TotalVersions"] = versions != null ? versions.Count : 0,
						["Versions"] = (versions ?? new List<VersionContent>()).Select(obj => obj.ToJson(json => (json as JObject).Remove("Data"))).ToJArray()
					},
					DeviceID = "*"
				}.Send();

			return versions;
		}

		/// <summary>
		/// Finds version contents of a collection of object
		/// </summary>
		/// <param name="objects"></param>
		/// <param name="cancellationToken"></param>
		/// <param name="sendUpdateMessages"></param>
		/// <returns></returns>
		public static async Task<Dictionary<string, List<VersionContent>>> FindVersionsAsync(this IEnumerable<RepositoryBase> objects, CancellationToken cancellationToken = default, bool sendUpdateMessages = true)
		{
			var versions = new Dictionary<string, List<VersionContent>>();
			if (objects != null && objects.Any())
				await Task.WhenAll(objects.Select(async @object =>
				{
					versions[@object.ID] = await @object.FindVersionsAsync(cancellationToken, sendUpdateMessages).ConfigureAwait(false);
				})).ConfigureAwait(false);
			return versions;
		}
		#endregion

	}
}