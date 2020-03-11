#region Related components
using System;
using System.Linq;
using System.Dynamic;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services
{
	public static partial class Extensions
	{

		#region Filter
		/// <summary>
		/// Prepares the comparing values of the filtering expression (means evaluating all JS expressions)
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filterBy"></param>
		/// <param name="current">The object that presents information of current processing object - '__current' global variable and 'this' instance is bond to JSON stringify</param>
		/// <param name="requestInfo">The object that presents the information - '__requestInfo' global variable</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		public static void Prepare<T>(this FilterBys<T> filterBy, object current = null, RequestInfo requestInfo = null, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null) where T : class
		{
			using (var jsEngine = Extensions.GetJsEngine(Extensions.GetJsEmbedObjects(current, requestInfo, embedObjects), Extensions.GetJsEmbedTypes(embedTypes)))
			{
				filterBy.Children.ForEach(filter =>
				{
					if (filter is FilterBys<T>)
						(filter as FilterBys<T>).Prepare(current, requestInfo, embedObjects, embedTypes);
					else if ((filter as FilterBy<T>).Value != null && (filter as FilterBy<T>).Value is string && ((filter as FilterBy<T>).Value as string).StartsWith("@"))
					{
						var jsExpression = Extensions.GetJsExpression((filter as FilterBy<T>).Value as string, current, requestInfo);
						(filter as FilterBy<T>).Value = jsEngine.JsEvaluate(jsExpression);
					}
				});
			}
		}

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
		/// Converts the JSON object to filtering expression
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
				name = serverJson.Get<string>("Attribute");
				return @operator.IsEquals("IsNull") || @operator.IsEquals("IsNotNull") || @operator.IsEquals("IsEmpty") || @operator.IsEquals("IsNotEmpty")
					? new JValue(@operator) as JToken
					: new JObject { { @operator, serverJson["Value"] as JValue } };
			}
			else
			{
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
		/// Gets UUID of this filtering definition
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter"></param>
		/// <returns></returns>
		public static string GetUUID<T>(this IFilterBy<T> filter) where T : class
			=> filter.ToClientJson().ToString(Formatting.None).ToLower().GenerateUUID();
		#endregion

		#region Sort
		/// <summary>
		/// Converts the JSON object to sorting expression
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
			clientJson[serverJson.Get<string>("Attribute")] = serverJson.Get("Mode", "Ascending");
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
		/// Gets UUID of this sorting definition
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="sortby"></param>
		/// <returns></returns>
		public static string GetUUID<T>(this SortBy<T> sortby) where T : class
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
			var totalPages = (int)(totalRecords / pageSize);
			if (totalRecords - (totalPages * pageSize) > 0)
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

	}
}