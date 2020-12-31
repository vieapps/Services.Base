#region Related components
using System;
using System.Linq;
using System.Dynamic;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Repository;
using JSPool;
using System.Diagnostics;
#endregion

namespace net.vieapps.Services
{
	public static partial class Extensions
	{

		#region Evaluate a formula
		/// <summary>
		/// Evaluates a Formula expression
		/// </summary>
		/// <param name="formula">The string that presents the formula</param>
		/// <param name="params">The parameters object for fetching data from (1st parameter is request information, 2nd parameter is current object, 3rd parameter is additional data)</param>
		/// <returns></returns>
		public static object Evaluate(this string formula, Tuple<ExpandoObject, ExpandoObject, ExpandoObject> @params)
		{
			// check
			if (string.IsNullOrWhiteSpace(formula) || !formula.StartsWith("@"))
				throw new InformationInvalidException($"The formula expression [{formula}] is invalid (the formula expression must started by the '@' character)");

			var position = formula.IndexOf("(");
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

			// value of request
			if (name.IsEquals("@request"))
				value = formula.StartsWith("@")
					? formula.Evaluate(@params)
					: @params.Item1?.Get(formula);

			// value of request's session
			else if (name.IsEquals("@session") || name.IsEquals("@request.Session"))
				value = formula.StartsWith("@")
					? formula.Evaluate(@params)
					: @params.Item1?.Get<ExpandoObject>("Session")?.Get(formula);

			// value of request's query
			else if (name.IsEquals("@query") || name.IsEquals("@request.Query"))
				value = formula.StartsWith("@")
					? formula.Evaluate(@params)
					: @params.Item1?.Get<ExpandoObject>("Query")?.Get(formula);

			// value of request's header
			else if (name.IsEquals("@header") || name.IsEquals("@request.Header"))
				value = formula.StartsWith("@")
					? formula.Evaluate(@params)
					: @params.Item1?.Get<ExpandoObject>("Header")?.Get(formula);

			// value of request's body
			else if (name.IsEquals("@body") || name.IsEquals("@request.Body"))
			{
				if (formula.StartsWith("@"))
					value = formula.Evaluate(@params);
				else
				{
					var body = @params.Item1?.Get("Body");
					value = (body is ExpandoObject bodyAsExpando ? bodyAsExpando : body is string bodyAsString ? bodyAsString?.ToExpandoObject() : null)?.Get(formula);
				}
			}

			// value of request's extra
			else if (name.IsEquals("@extra") || name.IsEquals("@request.Extra"))
				value = formula.StartsWith("@")
					? formula.Evaluate(@params)
					: @params.Item1?.Get<ExpandoObject>("Extra")?.Get(formula);

			// value of current object
			else if (name.IsEquals("@current") || name.IsEquals("@object"))
				value = formula.StartsWith("@")
					? formula.Evaluate(@params)
					: @params.Item2?.Get(formula);

			// value of additional parameters
			else if (name.IsEquals("@params") || name.IsEquals("@global"))
				value = formula.StartsWith("@")
					? formula.Evaluate(@params)
					: @params.Item3?.Get(formula);

			// value of an Javascript expression
			else if (name.IsEquals("@js") || name.IsEquals("@javascript") || name.IsEquals("@script"))
				value = Extensions.JsEvaluate(formula.GetJsExpression(@params.Item1, @params.Item2, @params.Item3));

			// pre-defined formula: current date-time
			else if (name.IsEquals("@now") || name.IsEquals("@datetime.Now") || name.IsEquals("@date.Now") || name.IsEquals("@time.Now"))
				value = name.IsEquals("@date.Now")
					? DateTime.Parse($"{DateTime.Now:yyyy/MM/dd} 00:00:00")
					: DateTime.Now;

			// pre-defined formula: get time quater
			else if ((name.IsEquals("@datetime.Quarter") || name.IsEquals("@time.Quarter")) && formula.StartsWith("@"))
			{
				var datetime = formula.Evaluate(@params) as DateTime?;
				value = datetime != null && datetime.HasValue ? datetime.Value.GetTimeQuarter() as DateTime? : null;
			}

			// pre-defined formula: convert current date-time to string
			else if (name.IsEquals("@today") || name.IsStartsWith("@todayStr") || name.IsEquals("@datetime.Today") || name.IsEquals("@date.Today") || name.IsEquals("@time.Today")
			|| name.IsStartsWith("@nowStr") || name.IsStartsWith("@datetime.NowStr") || name.IsStartsWith("@date.NowStr") || name.IsStartsWith("@time.NowStr"))
				value = DateTime.Now.ToDTString(false, name.IsStartsWith("@nowStr") || name.IsStartsWith("@datetime.NowStr") || name.IsStartsWith("@date.NowStr") || name.IsStartsWith("@time.NowStr"));

			// pre-defined formula: convert date-time to string
			else if ((name.IsStartsWith("@date") || name.IsStartsWith("@time")) && formula.StartsWith("@"))
			{
				var format = name.IsStartsWith("@time") ? "hh:mm tt @ dd/MM/yyyy" : "dd/MM/yyyy HH:mm:ss";
				position = formula.IndexOf("[");
				if (position > 0)
				{
					if (!formula.EndsWith("]"))
						throw new InformationInvalidException($"The formula expression [{formula}] is invalid (the open and close tokens are required when the formula got a formatting parameter), ex: @datetime.ToString(@current(Created)[dd/MM/yyyy hh:mm tt])");
					var temp = formula.Left(position).Trim();
					format = formula.Substring(position + 1, formula.Length - position - 2).Trim();
					formula = string.IsNullOrWhiteSpace(temp)  || temp.Equals("@") ? "@now" : temp;
				}
				var datetime = formula.Evaluate(@params) as DateTime?;
				value = datetime != null && datetime.HasValue ? datetime.Value.ToString(format) : null;
			}

			// convert the formula's value to string
			else if (name.IsStartsWith("@toStr") && formula.StartsWith("@"))
			{
				var format = "";
				position = formula.IndexOf("[");
				if (position > 0)
				{
					if (!formula.EndsWith("]"))
						throw new InformationInvalidException($"The formula expression [{formula}] is invalid (the open and close tokens are required when the formula got a formatting parameter), ex: @toString(@current(Created)[dd/MM/yyyy hh:mm tt])");
					var temp = formula.Left(position).Trim();
					format = formula.Substring(position + 1, formula.Length - position - 2).Trim();
					formula = string.IsNullOrWhiteSpace(temp) || temp.Equals("@") ? "@now" : temp;
				}
				value = formula.Evaluate(@params);
				value = value == null || string.IsNullOrWhiteSpace(format)
					? value?.ToString()
					: value.GetType().IsDateTimeType()
						? value.CastAs<DateTime>().ToString(format)
						: value.GetType().IsFloatingPointType()
							? value.CastAs<decimal>().ToString(format)
							: value.GetType().IsIntegralType()
								? value.CastAs<long>().ToString(format)
								: value.ToString();
			}

			// convert the formula's value to lower-case string
			else if (name.IsStartsWith("@toLower") && formula.StartsWith("@"))
				value = formula.Evaluate(@params)?.ToString().ToLower();

			// convert the formula's value to upper-case string
			else if (name.IsStartsWith("@toUpper") && formula.StartsWith("@"))
				value = formula.Evaluate(@params)?.ToString().ToUpper();

			// unknown => return the original formula
			else
				value = $"{name}{(string.IsNullOrWhiteSpace(formula) ? "" : $"({formula})")}";

			return value;
		}

		/// <summary>
		/// Evaluates a Formula expression
		/// </summary>
		/// <param name="formula">The string that presents the formula</param>
		/// <param name="requestInfo">The object that presents the information of the request information</param>
		/// <param name="object">The object for fetching data from</param>
		/// <param name="params">The additional parameters for fetching data from</param>
		/// <returns></returns>
		public static object Evaluate(this string formula, RequestInfo requestInfo = null, object @object = null, ExpandoObject @params = null)
			=> formula?.Evaluate(new Tuple<ExpandoObject, ExpandoObject, ExpandoObject>(requestInfo?.ToExpandoObject(requestInfoAsExpandoObject =>
			{
				requestInfoAsExpandoObject.Set("Body", requestInfo?.BodyAsExpandoObject);
				requestInfoAsExpandoObject.Get<ExpandoObject>("Header")?.Remove("x-app-token");
			}), @object?.ToExpandoObject(), @params));

		/// <summary>
		/// Gets the time quater
		/// </summary>
		/// <param name="time"></param>
		/// <param name="getHighValue"></param>
		/// <returns></returns>
		public static DateTime GetTimeQuarter(this DateTime time, bool getHighValue = true)
		{
			if (time.Minute <= 15)
				return getHighValue
					? DateTime.Parse($"{time:yyyy/MM/dd HH}:15:00")
					: DateTime.Parse($"{time:yyyy/MM/dd HH}:00:00");
			else if (time.Minute <= 30)
				return getHighValue
					? DateTime.Parse($"{time:yyyy/MM/dd HH}:30:00")
					: DateTime.Parse($"{time:yyyy/MM/dd HH}:16:00");
			else if (time.Minute <= 45)
				return getHighValue
					? DateTime.Parse($"{time:yyyy/MM/dd HH}:45:00")
					: DateTime.Parse($"{time:yyyy/MM/dd HH}:31:00");
			else
				return getHighValue
					? DateTime.Parse($"{time:yyyy/MM/dd HH}:59:59")
					: DateTime.Parse($"{time:yyyy/MM/dd HH}:46:00");
		}
		#endregion

		#region Filter
		/// <summary>
		/// Prepares the comparing values of the filtering expression (means evaluating all Formula/Javascript expressions)
		/// </summary>
		/// <param name="filterBy">The filtering expression</param>
		/// <param name="jsEngine">The pooled JavaScript engine for evaluating all JavaScript expressions</param>
		/// <param name="requestInfo">The object that presents the information of the request information</param>
		/// <param name="object">The object for fetching data from</param>
		/// <param name="params">The additional parameters for fetching data from</param>
		/// <param name="onCompleted">The action to run when the preparing process is completed</param>
		/// <returns>The filtering expression with all formula/expression values had been evaluated</returns>
		public static IFilterBy Prepare(this IFilterBy filterBy, PooledJsEngine jsEngine, ExpandoObject requestInfo, ExpandoObject @object, ExpandoObject @params, Action<IFilterBy> onCompleted = null)
		{
			// prepare value of a single filter
			if (filterBy is FilterBy filter)
			{
				if (filter?.Value != null && filter.Value is string value && value.StartsWith("@"))
				{
					// value of pre-defined objects/formulas
					if (value.IsStartsWith("@request(") || value.IsStartsWith("@request.") || value.IsStartsWith("@session(")
					|| value.IsStartsWith("@query(") || value.IsStartsWith("@header(") || value.IsStartsWith("@body(") || value.IsStartsWith("@extra(")
					|| value.IsStartsWith("@current(") || value.IsStartsWith("@object(") || value.IsStartsWith("@params(") || value.IsStartsWith("@global(")
					|| value.IsStartsWith("@js(") || value.IsStartsWith("@javascript(") || value.IsStartsWith("@script(")
					|| value.IsStartsWith("@today") || value.IsStartsWith("@now") || value.IsStartsWith("@date") || value.IsStartsWith("@time")
					|| value.IsStartsWith("@toStr") || value.IsStartsWith("@toLower") || value.IsStartsWith("@toUpper"))
						filter.Value = value.Evaluate(new Tuple<ExpandoObject, ExpandoObject, ExpandoObject>(requestInfo, @object, @params));

					// value of JavaScript expression
					else if (value.StartsWith("@[") && value.EndsWith("]"))
						filter.Value = jsEngine != null
							? jsEngine.JsEvaluate(value.GetJsExpression(requestInfo, @object, @params))
							: Extensions.JsEvaluate(value.GetJsExpression(requestInfo, @object, @params));
				}
			}

			// prepare children of a group filter
			else
				(filterBy as FilterBys)?.Children?.ForEach(filterby => filterby?.Prepare(jsEngine, requestInfo, @object, @params, onCompleted));

			// complete
			onCompleted?.Invoke(filterBy);
			return filterBy;
		}

		/// <summary>
		/// Prepares the comparing values of the filtering expression (means evaluating all Formula/Javascript expressions)
		/// </summary>
		/// <param name="filterBy">The filtering expression</param>
		/// <param name="requestInfo">The object that presents the information of the request information</param>
		/// <param name="jsEngine">The pooled JavaScript engine for evaluating all JavaScript expressions</param>
		/// <param name="object">The object for fetching data from</param>
		/// <param name="params">The additional parameters for fetching data from</param>
		/// <param name="onCompleted">The action to run when the preparing process is completed</param>
		/// <returns>The filtering expression with all formula/expression values had been evaluated</returns>
		public static IFilterBy Prepare(this IFilterBy filterBy, PooledJsEngine jsEngine = null, RequestInfo requestInfo = null, object @object = null, ExpandoObject @params = null, Action<IFilterBy> onCompleted = null)
			=> filterBy?.Prepare(jsEngine, requestInfo?.ToExpandoObject(requestInfoAsExpandoObject =>
			{
				requestInfoAsExpandoObject.Set("Body", requestInfo?.BodyAsExpandoObject);
				requestInfoAsExpandoObject.Get<ExpandoObject>("Header")?.Remove("x-app-token");
			}), @object?.ToExpandoObject(), @params, onCompleted);

		/// <summary>
		/// Prepares the comparing values of the filtering expression (means evaluating all Formula/Javascript expressions)
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filterBy">The filtering expression</param>
		/// <param name="jsEngine">The pooled JavaScript engine for evaluating all JavaScript expressions</param>
		/// <param name="requestInfo">The object that presents the information of the request information</param>
		/// <param name="object">The object for fetching data from</param>
		/// <param name="params">The additional parameters for fetching data from</param>
		/// <param name="onCompleted">The action to run when the preparing process is completed</param>
		/// <returns>The filtering expression with all formula/expression values had been evaluated</returns>
		public static IFilterBy<T> Prepare<T>(this IFilterBy<T> filterBy, PooledJsEngine jsEngine = null, RequestInfo requestInfo = null, object @object = null, ExpandoObject @params = null, Action<IFilterBy<T>> onCompleted = null) where T : class
			=> (filterBy as IFilterBy)?.Prepare(jsEngine, requestInfo, @object, @params, onCompleted as Action<IFilterBy>) as IFilterBy<T>;

		/// <summary>
		/// Prepares the comparing values of the filtering expression (means evaluating all Formula/Javascript expressions)
		/// </summary>
		/// <param name="filterBy">The filtering expression</param>
		/// <param name="requestInfo">The object that presents the information of the request information</param>
		/// <param name="object">The object for fetching data from</param>
		/// <param name="params">The additional parameters for fetching data from</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <param name="onCompleted">The action to run when the preparing process is completed</param>
		/// <returns>The filtering expression with all formula/expression values had been evaluated</returns>
		public static IFilterBy Prepare(this IFilterBy filterBy, RequestInfo requestInfo, object @object, ExpandoObject @params = null, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null, Action<IFilterBy, PooledJsEngine> onCompleted = null)
		{
			using (var jsEngine = Extensions.GetJsEngine(embedObjects, embedTypes))
			{
				return filterBy?.Prepare(jsEngine, requestInfo, @object, @params, filter => onCompleted?.Invoke(filter, jsEngine));
			}
		}

		/// <summary>
		/// Prepares the comparing values of the filtering expression (means evaluating all Formula/Javascript expressions)
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filterBy">The filtering expression</param>
		/// <param name="requestInfo">The object that presents the information of the request information</param>
		/// <param name="object">The object for fetching data from</param>
		/// <param name="params">The additional parameters for fetching data from</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <param name="onCompleted">The action to run when the preparing process is completed</param>
		/// <returns>The filtering expression with all formula/expression values had been evaluated</returns>
		public static IFilterBy<T> Prepare<T>(this IFilterBy<T> filterBy, RequestInfo requestInfo, object @object, ExpandoObject @params = null, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null, Action<IFilterBy<T>, PooledJsEngine> onCompleted = null) where T : class
			=> (filterBy as IFilterBy)?.Prepare(requestInfo, @object, @params, embedObjects, embedTypes, onCompleted as Action<IFilterBy, PooledJsEngine>) as IFilterBy<T>;

		/// <summary>
		/// Prepares the comparing values of the filtering expression (means evaluating all Formula/Javascript expressions)
		/// </summary>
		/// <param name="filterBy">The filtering expression</param>
		/// <param name="requestInfo">The object that presents the information of the request information</param>
		/// <param name="onCompleted">The action to run when the preparing process is completed</param>
		/// <returns>The filtering expression with all formula/expression values had been evaluated</returns>
		public static IFilterBy Prepare(this IFilterBy filterBy, RequestInfo requestInfo, Action<IFilterBy> onCompleted = null)
		{
			filterBy?.Prepare(null, requestInfo, null, null, null);
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
			filterBy?.Prepare<T>(null, requestInfo, null, null, null);
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

			// group comparisions
			if (attribute.IsEquals("And") || attribute.IsEquals("Or"))
			{
				filter = attribute.IsEquals("Or") ? Filters<T>.Or() : Filters<T>.And();
				(property.Value is JObject ? (property.Value as JObject).ToJArray(kvp => new JObject { { kvp.Key, kvp.Value } }) : property.Value as JArray).ForEach(exp => (filter as FilterBys<T>).Add(exp != null && exp is JObject ? (exp as JObject).GetFilterBy<T>() : null));
				if ((filter as FilterBys<T>).Children.Count < 1)
					filter = null;
			}

			// single comparisions
			else
			{
				var @operator = "";
				var value = JValue.CreateNull();

				// special comparison
				if (property.Value is JValue)
				{
					@operator = (property.Value as JValue).Value.ToString();
					if (!@operator.IsEquals("IsNull") && !@operator.IsEquals("IsNotNull") && !@operator.IsEquals("IsEmpty") && !@operator.IsEquals("IsNotEmpty"))
						@operator = null;
				}

				// normal comparison
				else if (property.Value is JObject)
				{
					property = (property.Value as JObject).Properties()?.FirstOrDefault();
					if (property != null && property.Value != null && property.Value is JValue && (property.Value as JValue).Value != null)
					{
						@operator = property.Name;
						value = property.Value as JValue;
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
				(children is JObject ? (children as JObject).ToJArray(kvp => new JObject
				{
					{ kvp.Key, kvp.Value }
				}) : children as JArray).ForEach(exp => filter.Add(exp != null && exp is JObject ? (exp as JObject).GetFilterBy<T>() : null));
			}

			return filter != null && filter.Children.Count > 0 ? filter : null;
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
						{ @operator, value }
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
			=> !string.IsNullOrWhiteSpace(expression?.Get<string>("Attribute"))
				? new SortBy<T>(expression)
				: null;

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
				? totalRecords > 0
					? Extensions.GetTotalPages(totalRecords, pageSize)
					: 0
				: totalPages;

			var pageNumber = pagination.Get("PageNumber", 1);
			pageNumber = pageNumber < 1
				? 1
				: totalPages > 0 && pageNumber > totalPages
					? totalPages
					: pageNumber;

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
		/// <returns></returns>
		public static string GetCacheKey(string prefix, int pageSize = 0, int pageNumber = 0, bool addPageNumberHolder = false)
			=> $"{prefix}{(pageNumber > 0 ? $"#p:{(addPageNumberHolder ? "{{pageNumber}}" : $"{pageNumber}")}{(pageSize > 0 ? $"~{pageSize}" : "")}" : "")}";

		/// <summary>
		/// Gets the caching key
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="prefix">The string that presents the prefix of the caching key</param>
		/// <param name="pageSize">The page size</param>
		/// <param name="pageNumber">The page number</param>
		/// <param name="addPageNumberHolder">true to add page number as '[page-number]' holder</param>
		/// <returns></returns>
		public static string GetCacheKey<T>(string prefix, int pageSize = 0, int pageNumber = 0, bool addPageNumberHolder = false) where T : class
			=> $"{Extensions.GetCacheKey<T>()}{Extensions.GetCacheKey(prefix, pageSize, pageNumber, addPageNumberHolder)}";

		/// <summary>
		/// Gets the caching key
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter">The filter expression</param>
		/// <param name="sort">The sort expression</param>
		/// <param name="pageSize">The page size</param>
		/// <param name="pageNumber">The page number</param>
		/// <param name="addPageNumberHolder">true to add page number as '[page-number]' holder</param>
		/// <returns></returns>
		public static string GetCacheKey<T>(IFilterBy<T> filter, SortBy<T> sort, int pageSize = 0, int pageNumber = 0, bool addPageNumberHolder = false) where T : class
			=> Extensions.GetCacheKey<T>($"{(filter != null ? $"#f:{filter.GenerateUUID()}" : "")}{(sort != null ? $"#s:{sort.GenerateUUID()}" : "")}", pageSize, pageNumber, addPageNumberHolder);

		static List<string> KeyPatterns => "total,json,xml".ToList();

		static List<string> RelatedKeyPatterns => "thumbnails,attachments,relateds,newers,olders,others".ToList();

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
				var pageKey = paginationKey.Replace("{{pageNumber}}", $"{pageNumber}");
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
		/// <returns>The string that presents a caching key</returns>
		public static string GetCacheKeyOfTotalObjects<T>(string prefix) where T : class
			=> $"{Extensions.GetCacheKey<T>(prefix)}:total";

		/// <summary>
		/// Gets the caching key for workingwith the number of total objects
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter">The filter expression</param>
		/// <param name="sort">The sort expression</param>
		/// <returns>The string that presents a caching key</returns>
		public static string GetCacheKeyOfTotalObjects<T>(IFilterBy<T> filter, SortBy<T> sort) where T : class
			=> Extensions.GetCacheKeyOfTotalObjects<T>((filter != null ? $"#f:{filter.GenerateUUID()}" : "") + (sort != null ? $"#s:{sort.GenerateUUID()}" : ""));

		/// <summary>
		/// Gets the caching key for working with the JSON of objects
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="prefix">The string that presents the prefix of the caching key</param>
		/// <param name="pageNumber">The page number</param>
		/// <param name="pageSize">The page size</param>
		/// <returns>The string that presents a caching key</returns>
		public static string GetCacheKeyOfObjectsJson<T>(string prefix, int pageSize = 0, int pageNumber = 0) where T : class
			=> $"{Extensions.GetCacheKey<T>(prefix, pageSize, pageNumber)}:json";

		/// <summary>
		/// Gets the caching key for working with the JSON of objects
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter">The filter expression</param>
		/// <param name="sort">The sort expression</param>
		/// <param name="pageNumber">The page number</param>
		/// <param name="pageSize">The page size</param>
		/// <returns>The string that presents a caching key</returns>
		public static string GetCacheKeyOfObjectsJson<T>(IFilterBy<T> filter, SortBy<T> sort, int pageSize = 0, int pageNumber = 0) where T : class
			=> Extensions.GetCacheKeyOfObjectsJson<T>((filter != null ? $"#f:{filter.GenerateUUID()}" : "") + (sort != null ? $"#s:{sort.GenerateUUID()}" : ""), pageSize, pageNumber);

		/// <summary>
		/// Gets the caching key for working with the XML of objects
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="prefix">The string that presents the prefix of the caching key</param>
		/// <param name="pageNumber">The page number</param>
		/// <param name="pageSize">The page size</param>
		/// <returns>The string that presents a caching key</returns>
		public static string GetCacheKeyOfObjectsXml<T>(string prefix, int pageSize = 0, int pageNumber = 0) where T : class
			=> $"{Extensions.GetCacheKey<T>(prefix, pageSize, pageNumber)}:xml";

		/// <summary>
		/// Gets the caching key for working with the XML of objects
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter">The filter expression</param>
		/// <param name="sort">The sort expression</param>
		/// <param name="pageNumber">The page number</param>
		/// <param name="pageSize">The page size</param>
		/// <returns>The string that presents a caching key</returns>
		public static string GetCacheKeyOfObjectsXml<T>(IFilterBy<T> filter, SortBy<T> sort, int pageSize = 0, int pageNumber = 0) where T : class
			=> Extensions.GetCacheKeyOfObjectsXml<T>((filter != null ? $"#f:{filter.GenerateUUID()}" : "") + (sort != null ? $"#s:{sort.GenerateUUID()}" : ""), pageSize, pageNumber);
		#endregion

	}
}