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

		#region Filter
		/// <summary>
		/// Evaluates a formula
		/// </summary>
		/// <param name="formula">The string that presents the formula</param>
		/// <param name="object">The object for fetching data from</param>
		/// <param name="session">The session for fetching data from</param>
		/// <param name="query">The query for fetching data from</param>
		/// <param name="header">The header for fetching data from</param>
		/// <param name="body">The body for fetching data from</param>
		/// <param name="extra">The extra for fetching data from</param>
		/// <param name="params">The additional parameters for fetching data from</param>
		/// <returns></returns>
		public static object Evaluate(this string formula, object @object = null, ExpandoObject session = null, ExpandoObject query = null, ExpandoObject header = null, ExpandoObject body = null, ExpandoObject extra = null, ExpandoObject @params = null)
		{
			// check
			if (string.IsNullOrWhiteSpace(formula) || !formula.StartsWith("@"))
				throw new InformationInvalidException("The formula is invalid (the formula must start with at (@) character)");

			var position = formula.IndexOf("[");
			if (position > 0 && !formula.EndsWith("]"))
				throw new InformationInvalidException("The formula is invalid (open and close token is required when the formula got parameter (ex: @query[x-content-id] - just like a Javascript function)");

			// prepare
			object value = null;
			var name = formula;
			if (position > 0)
			{
				name = formula.Left(position);
				formula = formula.Substring(position + 1, formula.Length - position - 2);
			}

			// value of current object
			if (name.IsEquals("@current") || name.IsEquals("@object"))
				value = formula.StartsWith("@") ? formula.Evaluate(@object, session, query, header, body, extra, @params) : @object?.GetAttributeValue(formula);

			// value of session
			else if (name.IsEquals("@session"))
				value = formula.StartsWith("@") ? formula.Evaluate(@object, session, query, header, body, extra, @params) : session?.Get(formula);

			// value of query
			else if (name.IsEquals("@query"))
				value = formula.StartsWith("@") ? formula.Evaluate(@object, session, query, header, body, extra, @params) : query?.Get(formula);

			// value of header
			else if (name.IsEquals("@header"))
				value = formula.StartsWith("@") ? formula.Evaluate(@object, session, query, header, body, extra, @params) : header?.Get(formula);

			// value of body
			else if (name.IsEquals("@body"))
				value = formula.StartsWith("@") ? formula.Evaluate(@object, session, query, header, body, extra, @params) : body?.Get(formula);

			// value of extra
			else if (name.IsEquals("@extra"))
				value = formula.StartsWith("@") ? formula.Evaluate(@object, session, query, header, body, extra, @params) : extra?.Get(formula);

			// value of additional params
			else if (name.IsEquals("@params"))
				value = formula.StartsWith("@") ? formula.Evaluate(@object, session, query, header, body, extra, @params) : @params?.Get(formula);

			// pre-defined formula: today date-time => string with format yyyy/MM/dd
			else if (name.IsEquals("@today") || name.IsStartsWith("@todayStr"))
				value = DateTime.Now.ToDTString(false, false);

			// pre-defined formula: current date-time
			else if (name.IsEquals("@now"))
				value = DateTime.Now;

			// pre-defined formula: current date-time => string with format yyyy/MM/dd HH:mm:ss
			else if (name.IsStartsWith("@nowStr"))
				value = DateTime.Now.ToDTString(false, true);

			// convert the formula's value to string
			else if (name.IsStartsWith("@toStr") && formula.StartsWith("@"))
				value = formula.Evaluate(@object, session, query, header, body, extra, @params)?.ToString();

			return value;
		}

		/// <summary>
		/// Evaluates a formula
		/// </summary>
		/// <param name="formula">The string that presents the formula</param>
		/// <param name="object">The object for fetching data from</param>
		/// <param name="requestInfo">The object that presents the information of the request information</param>
		/// <param name="params">The additional parameters for fetching data from</param>
		/// <returns></returns>
		public static object Evaluate(this string formula, object @object = null, RequestInfo requestInfo = null, ExpandoObject @params = null)
			=> !string.IsNullOrWhiteSpace(formula)
				? formula.Evaluate(@object, requestInfo?.Session?.ToExpandoObject(), requestInfo?.Query?.ToExpandoObject(), requestInfo?.Header?.ToExpandoObject(), requestInfo?.Body?.ToExpandoObject(), requestInfo?.Extra?.ToExpandoObject(), @params)
				: null;

		/// <summary>
		/// Prepares the comparing values of the filtering expression (means evaluating all formulas/JavaScript expressions)
		/// </summary>
		/// <param name="filterBy">The filtering expression</param>
		/// <param name="jsEngine">The pooled JavaScript engine for evaluating all JavaScript expressions</param>
		/// <param name="current">The object that presents information of current processing object (in JS expression, that is '__current' global variable and 'this' instance is bound to JSON stringify)</param>
		/// <param name="requestInfo">The object that presents the information of the request information (in JS expression, that is '__requestInfo' global variable)</param>
		/// <param name="onCompleted">The action to run when the preparing process is completed</param>
		/// <returns>The filtering expression with all formula/expression values had been evaluated</returns>
		public static IFilterBy Prepare(this IFilterBy filterBy, PooledJsEngine jsEngine = null, object current = null, RequestInfo requestInfo = null, Action<IFilterBy> onCompleted = null)
		{
			ExpandoObject session = null, query = null, header = null, body = null, extra = null;

			// prepare value of a single filter
			if (filterBy is FilterBy filter)
			{
				if (filter?.Value != null && filter.Value is string value && value.StartsWith("@"))
				{
					// value of pre-defined objects/formulas
					if (value.IsStartsWith("@current[") || value.IsStartsWith("@object[") || value.IsStartsWith("@session[") || value.IsStartsWith("@query[") || value.IsStartsWith("@header[") || value.IsStartsWith("@body[") || value.IsStartsWith("@extra[") || value.IsStartsWith("@today") || value.IsStartsWith("@now") || value.IsStartsWith("@toStr"))
					{
						session = session ?? requestInfo?.Session?.ToExpandoObject();
						query = query ?? requestInfo?.Query?.ToExpandoObject();
						header = header ?? requestInfo?.Header?.ToExpandoObject();
						body = body ?? requestInfo?.Body?.ToExpandoObject();
						extra = extra ?? requestInfo?.Extra?.ToExpandoObject();
						filter.Value = value.Evaluate(current, session, query, header, body, extra);
					}

					// value of JavaScript expression
					else if (jsEngine != null && !value.StartsWith("@["))
					{
						var jsExpression = Extensions.GetJsExpression(value, current, requestInfo);
						filter.Value = jsEngine.JsEvaluate(jsExpression);
					}
				}
			}

			// prepare children of a group filter
			else
				(filterBy as FilterBys)?.Children?.ForEach(filterby => filterby?.Prepare(jsEngine, current, requestInfo, onCompleted));

			// complete
			onCompleted?.Invoke(filterBy);
			return filterBy;
		}

		/// <summary>
		/// Prepares the comparing values of the filtering expression (means evaluating all formulas/JavaScript expressions)
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filterBy">The filtering expression</param>
		/// <param name="jsEngine">The pooled JavaScript engine for evaluating all JavaScript expressions</param>
		/// <param name="current">The object that presents information of current processing object (in JS expression, that is '__current' global variable and 'this' instance is bound to JSON stringify)</param>
		/// <param name="requestInfo">The object that presents the information of the request information (in JS expression, that is '__requestInfo' global variable)</param>
		/// <param name="onCompleted">The action to run when the preparing process is completed</param>
		/// <returns>The filtering expression with all formula/expression values had been evaluated</returns>
		public static IFilterBy<T> Prepare<T>(this IFilterBy<T> filterBy, PooledJsEngine jsEngine = null, object current = null, RequestInfo requestInfo = null, Action<IFilterBy<T>> onCompleted = null) where T : class
			=> (filterBy as IFilterBy)?.Prepare(jsEngine, current, requestInfo, onCompleted as Action<IFilterBy>) as IFilterBy<T>;

		/// <summary>
		/// Prepares the comparing values of the filtering expression (means evaluating all formulas/JavaScript expressions)
		/// </summary>
		/// <param name="filterBy">The filtering expression</param>
		/// <param name="current">The object that presents information of current processing object (in JS expression, that is '__current' global variable and 'this' instance is bound to JSON stringify)</param>
		/// <param name="requestInfo">The object that presents the information of the request information (in JS expression, that is '__requestInfo' global variable)</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <param name="onCompleted">The action to run when the preparing process is completed</param>
		/// <returns>The filtering expression with all formula/expression values had been evaluated</returns>
		public static IFilterBy Prepare(this IFilterBy filterBy, object current, RequestInfo requestInfo = null, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null, Action<IFilterBy, PooledJsEngine> onCompleted = null)
		{
			using (var jsEngine = Extensions.GetJsEngine(Extensions.GetJsEmbedObjects(current, requestInfo, embedObjects), Extensions.GetJsEmbedTypes(embedTypes)))
			{
				return filterBy?.Prepare(jsEngine, current, requestInfo, filter => onCompleted?.Invoke(filter, jsEngine));
			}
		}

		/// <summary>
		/// Prepares the comparing values of the filtering expression (means evaluating all formulas/JavaScript expressions)
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filterBy">The filtering expression</param>
		/// <param name="current">The object that presents information of current processing object (in JS expression, that is '__current' global variable and 'this' instance is bound to JSON stringify)</param>
		/// <param name="requestInfo">The object that presents the information of the request information (in JS expression, that is '__requestInfo' global variable)</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <param name="onCompleted">The action to run when the preparing process is completed</param>
		/// <returns>The filtering expression with all formula/expression values had been evaluated</returns>
		public static IFilterBy<T> Prepare<T>(this IFilterBy<T> filterBy, object current, RequestInfo requestInfo = null, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null, Action<IFilterBy<T>, PooledJsEngine> onCompleted = null) where T : class
			=> (filterBy as IFilterBy)?.Prepare(current, requestInfo, embedObjects, embedTypes, onCompleted as Action<IFilterBy, PooledJsEngine>) as IFilterBy<T>;

		/// <summary>
		/// Prepares the comparing values of the filtering expression (means evaluating all formulas/JavaScript expressions)
		/// </summary>
		/// <param name="filterBy">The filtering expression</param>
		/// <param name="requestInfo">The object that presents the information of the request information</param>
		/// <param name="onCompleted">The action to run when the preparing process is completed</param>
		/// <returns>The filtering expression with all formula/expression values had been evaluated</returns>
		public static IFilterBy Prepare(this IFilterBy filterBy, RequestInfo requestInfo, Action<IFilterBy> onCompleted = null)
		{
			filterBy?.Prepare(null, null, requestInfo, null);
			onCompleted?.Invoke(filterBy);
			return filterBy;
		}

		/// <summary>
		/// Prepares the comparing values of the filtering expression (means evaluating all formulas/JavaScript expressions)
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filterBy">The filtering expression</param>
		/// <param name="requestInfo">The object that presents the information of the request information</param>
		/// <param name="onCompleted">The action to run when the preparing process is completed</param>
		/// <returns>The filtering expression with all formula/expression values had been evaluated</returns>
		public static IFilterBy<T> Prepare<T>(this IFilterBy<T> filterBy, RequestInfo requestInfo, Action<IFilterBy<T>> onCompleted = null) where T : class
		{
			filterBy?.Prepare<T>(null, null, requestInfo, null);
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
		/// Converts the (client) JSON object to filtering expression
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
		/// Converts the Expando object to filtering expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="expression"></param>
		/// <returns></returns>
		public static IFilterBy<T> ToFilterBy<T>(this ExpandoObject expression) where T : class
			=> JObject.FromObject(expression).ToFilterBy<T>();

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
			=> filter.ToClientJson().ToString(Formatting.None).ToLower().GenerateUUID();
		#endregion

		#region Sort
		/// <summary>
		/// Converts the (client) JSON object to sorting expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="expression"></param>
		/// <returns></returns>
		public static SortBy<T> ToSortBy<T>(this JObject expression) where T : class
		{
			SortBy<T> sort = null;
			expression.ForEach(kvp =>
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
		/// Converts the Expando object to sorting expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="expression"></param>
		/// <returns></returns>
		public static SortBy<T> ToSortBy<T>(this ExpandoObject expression) where T : class
			=> JObject.FromObject(expression).ToSortBy<T>();

		static void GetClientJson(this JToken serverJson, JObject clientJson)
		{
			var attribute = serverJson.Get<string>("Attribute");
			if (!string.IsNullOrWhiteSpace(attribute))
				clientJson[attribute] = serverJson.Get("Mode", "Ascending");
			serverJson.Get<JObject>("ThenBy")?.GetClientJson(clientJson);
		}

		/// <summary>
		/// Converts the sorting expression to JSON for using at client-side
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="sort"></param>
		/// <returns></returns>
		public static JObject ToClientJson<T>(this SortBy<T> sort) where T : class
		{
			var clientJson = new JObject();
			sort.ToJson().GetClientJson(clientJson);
			return clientJson;
		}

		/// <summary>
		/// Generates the UUID of this sort expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="sortby"></param>
		/// <returns></returns>
		public static string GenerateUUID<T>(this SortBy<T> sortby) where T : class
			=> sortby.ToClientJson().ToString(Formatting.None).ToLower().GenerateUUID();
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
			var totalRecords = pagination["TotalRecords"] != null && pagination["TotalRecords"] is JValue && (pagination["TotalRecords"] as JValue).Value != null
				? (pagination["TotalRecords"] as JValue).Value.CastAs<long>()
				: -1;

			var pageSize = pagination["PageSize"] != null && pagination["PageSize"] is JValue && (pagination["PageSize"] as JValue).Value != null
				? (pagination["PageSize"] as JValue).Value.CastAs<int>()
				: 20;
			if (pageSize < 0)
				pageSize = 20;

			var totalPages = pagination["TotalPages"] != null && pagination["TotalPages"] is JValue && (pagination["TotalPages"] as JValue).Value != null
				? (pagination["TotalPages"] as JValue).Value.CastAs<int>()
				: -1;
			if (totalPages < 0)
				totalPages = Extensions.GetTotalPages(totalRecords, pageSize);

			var pageNumber = pagination["PageNumber"] != null && pagination["PageNumber"] is JValue && (pagination["PageNumber"] as JValue).Value != null
				? (pagination["PageNumber"] as JValue).Value.CastAs<int>()
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
		/// <param name="addPageNumberHolder">true to add page number as '[page-number]' holder</param>
		/// <returns></returns>
		public static string GetCacheKey(string prefix, int pageSize = 0, int pageNumber = 0, bool addPageNumberHolder = false)
			=> $"{prefix}{(pageNumber > 0 ? $"#p:{(addPageNumberHolder ? "[page-number]" : pageNumber.ToString())}{(pageSize > 0 ? $"~{pageSize}" : "")}" : "")}";

		/// <summary>
		/// Gets the caching key
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter">The filter expression</param>
		/// <param name="sort">The sort expression</param>
		/// <param name="pageSize">The page size</param>
		/// <param name="pageNumber">The page number</param>
		/// <returns></returns>
		public static string GetCacheKey<T>(IFilterBy<T> filter, SortBy<T> sort, int pageSize = 0, int pageNumber = 0) where T : class
			=> Extensions.GetCacheKey(Extensions.GetCacheKey<T>() + $"{(filter != null ? $"#f:{filter.GenerateUUID()}" : "")}{(sort != null ? $"#s:{sort.GenerateUUID()}" : "")}", pageSize, pageNumber);

		/// <summary>
		/// Gets the related caching key for working with collection of objects
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter">The filter expression</param>
		/// <param name="sort">The sort expression</param>
		/// <returns>The collection presents all related caching keys (100 pages each size is 20 objects)</returns>
		public static List<string> GetRelatedCacheKeys<T>(IFilterBy<T> filter, SortBy<T> sort) where T : class
		{
			var key = Extensions.GetCacheKey(filter, sort);
			var keys = new List<string> { key, $"{key}:total", Extensions.GetCacheKey(key, 0, 1) };
			var paginationKey = Extensions.GetCacheKey(key, 20, 1, true);
			for (var pageNumber = 1; pageNumber <= 100; pageNumber++)
			{
				var pageKey = paginationKey.Replace("[page-number]", pageNumber.ToString());
				keys.Add(pageKey);
				keys.Add($"{pageKey}:json");
				keys.Add($"{pageKey}:xml");
			}
			return keys;
		}

		/// <summary>
		/// Gets the caching key for workingwith the number of total objects
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter">The filter expression</param>
		/// <param name="sort">The sort expression</param>
		/// <returns>The string that presents a caching key</returns>
		public static string GetCacheKeyOfTotalObjects<T>(IFilterBy<T> filter, SortBy<T> sort) where T : class
			=> $"{Extensions.GetCacheKey(filter, sort)}:total";

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
			=> $"{Extensions.GetCacheKey(filter, sort, pageSize, pageNumber)}:json";

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
			=> $"{Extensions.GetCacheKey(filter, sort, pageSize, pageNumber)}:xml";
		#endregion

	}
}