#region Related components
using System;
using System.IO;
using System.Net;
using System.Web;
using System.Linq;
using System.Dynamic;
using System.Collections.Generic;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Extension methods for working with services
	/// </summary>
	public static class Extensions
	{

		#region Filter
		static FilterBy<T> GetFilterBy<T>(string attribute, string @operator, JValue value) where T : class
		{
			return new FilterBy<T>(new JObject()
			{
				{ "Attribute", attribute },
				{ "Operator", @operator },
				{ "Value", value }
			});
		}

		static FilterBys<T> GetFilterBys<T>(string @operator, JObject children) where T : class
		{
			var childFilters = new List<IFilterBy<T>>();
			foreach (var info in children)
			{
				IFilterBy<T> filter = null;
				var name = info.Key;
				var value = info.Value;

				// child expressions
				if (value is JObject && (name.IsEquals("And") || name.IsEquals("Or")))
					filter = Extensions.GetFilterBys<T>(name, value as JObject);

				// special comparisons
				else if (value is JValue)
				{
					var op = (value as JValue).Value.ToString();
					if (op.IsEquals("IsNull") || op.IsEquals("IsNotNull") || op.IsEquals("IsEmpty") || op.IsEquals("IsNotEmpty"))
						filter = Extensions.GetFilterBy<T>(name, op, null);
				}

				// normal comparison
				else if (value is JObject)
				{
					var prop = (value as JObject).Properties().FirstOrDefault();
					if (prop != null && prop.Value != null && prop.Value is JValue && (prop.Value as JValue).Value != null)
						filter = Extensions.GetFilterBy<T>(name, prop.Name, prop.Value as JValue);
				}

				if (filter != null)
					childFilters.Add(filter);
			}
			return new FilterBys<T>(@operator.ToEnum<GroupOperator>(), childFilters);
		}

		/// <summary>
		/// Converts the JSON object to filtering expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filterby"></param>
		/// <returns></returns>
		public static IFilterBy<T> ToFilterBy<T>(this JObject filterby) where T : class
		{
			var orFilters = filterby["Or"] as JObject;
			var andFilters = filterby["And"] as JObject;
			var rootFilters = orFilters != null
				? orFilters
				: andFilters != null
					? andFilters
					: filterby;

			var filters = orFilters != null
				? Filters<T>.Or()
				: Filters<T>.And();

			foreach (var info in rootFilters)
				if (!info.Key.Equals("") && !info.Key.IsEquals("Query"))
				{
					IFilterBy<T> filter = null;
					var name = info.Key;
					var value = info.Value;

					// child expressions
					if (value is JObject && (name.IsEquals("And") || name.IsEquals("Or")))
						filter = Extensions.GetFilterBys<T>(name, value as JObject);

					// special comparisions
					else if (value is JValue)
					{
						var op = (value as JValue).Value.ToString();
						if (op.IsEquals("IsNull") || op.IsEquals("IsNotNull") || op.IsEquals("IsEmpty") || op.IsEquals("IsNotEmpty"))
							filter = Extensions.GetFilterBy<T>(name, op, null);
					}

					// normal comparisions
					else if (value is JObject)
					{
						var prop = (value as JObject).Properties().FirstOrDefault();
						if (prop != null && prop.Value != null && prop.Value is JValue && (prop.Value as JValue).Value != null)
							filter = Extensions.GetFilterBy<T>(name, prop.Name, prop.Value as JValue);
					}

					if (filter != null)
						filters.Add(filter);
				}

			return filters != null && filters.Children.Count > 0
				? filters
				: null;
		}

		/// <summary>
		/// Converts the Expando object to filtering expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filterby"></param>
		/// <returns></returns>
		public static IFilterBy<T> ToFilterBy<T>(this ExpandoObject filterby) where T : class
		{
			return Extensions.ToFilterBy<T>(JObject.FromObject(filterby));
		}

		static JProperty AddClientJson(JObject serverJson)
		{
			var @operator = (serverJson["Operator"] as JValue).Value.ToString();
			if (serverJson["Children"] == null)
			{
				var token = @operator.IsEquals("IsNull") || @operator.IsEquals("IsNotNull") || @operator.IsEquals("IsEmpty") || @operator.IsEquals("IsNotEmpty")
					? new JValue(@operator) as JToken
					: new JObject()
					{
						{
							@operator,
							new JValue((serverJson["Value"] as JValue)?.Value)
						}
					} as JToken;
				return new JProperty((serverJson["Attribute"] as JValue).Value.ToString(), token);
			}
			else
			{
				var children = serverJson["Children"] as JArray;
				if (children == null || children.Count < 1)
					return null;

				var clientJson = new JObject();
				foreach(JObject childJson in children)
					clientJson.Add(Extensions.AddClientJson(childJson));
				return new JProperty(@operator, clientJson);
			}
		}

		/// <summary>
		/// Converts the filtering expression to JSON for using at client-side
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filterby"></param>
		/// <param name="query"></param>
		/// <returns></returns>
		public static JObject ToClientJson<T>(this IFilterBy<T> filterby, string query = null) where T : class
		{
			var clientJson = new JObject();
			if (!string.IsNullOrEmpty(query))
				clientJson.Add(new JProperty("Query", query));

			var json = Extensions.AddClientJson(filterby.ToJson());
			if (json != null)
				clientJson.Add(json);

			return clientJson;
		}

		/// <summary>
		/// Gets MD5 hash
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filterby"></param>
		/// <returns></returns>
		public static string GetMD5<T>(this IFilterBy<T> filterby) where T : class
		{
			return filterby != null
				? filterby.ToClientJson().ToString(Formatting.None).ToLower().GetMD5()
				: "";
		}
		#endregion

		#region Sort
		/// <summary>
		/// Converts the JSON object to sorting expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="sortby"></param>
		/// <returns></returns>
		public static SortBy<T> ToSortBy<T>(this JObject sortby) where T : class
		{
			SortBy<T> sort = null;
			foreach (var info in sortby)
				if (!info.Key.Equals(""))
				{
					var attribute = info.Key;
					var mode = (info.Value as JValue).Value.ToString().ToEnum<SortMode>();

					sort = sort != null
						? mode.Equals(SortMode.Ascending)
							? sort.ThenByAscending(attribute)
							: sort.ThenByDescending(attribute)
						: mode.Equals(SortMode.Ascending)
							? Sorts<T>.Ascending(attribute)
							: Sorts<T>.Descending(attribute);
				}
			return sort;
		}

		/// <summary>
		/// Converts the Expando object to sorting expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="sortby"></param>
		/// <returns></returns>
		public static SortBy<T> ToSortBy<T>(this ExpandoObject sortby) where T : class
		{
			return Extensions.ToSortBy<T>(JObject.FromObject(sortby));
		}

		static void AddClientJson(JObject clientJson, JObject serverJson)
		{
			var attribute = (serverJson["Attribute"] as JValue).Value.ToString();
			var mode = (serverJson["Mode"] as JValue).Value.ToString();
			clientJson.Add(new JProperty(attribute, mode));

			var thenby = serverJson["ThenBy"];
			if (thenby != null && thenby is JObject)
				Extensions.AddClientJson(clientJson, thenby as JObject);
		}

		/// <summary>
		/// Converts the sorting expression to JSON for using at client-side
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="sortby"></param>
		/// <returns></returns>
		public static JObject ToClientJson<T>(this SortBy<T> sortby) where T : class
		{
			var clientJson = new JObject();
			Extensions.AddClientJson(clientJson, sortby.ToJson());
			return clientJson;
		}

		/// <summary>
		/// Gets MD5 hash
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="sortby"></param>
		/// <returns></returns>
		public static string GetMD5<T>(this SortBy<T> sortby) where T : class
		{
			return sortby != null
				? sortby.ToClientJson().ToString(Formatting.None).ToLower().GetMD5()
				: "";
		}
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
		{
			return Extensions.GetTotalPages(info.Item1, info.Item2);
		}

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

			var pageSize = pagination.Get<int>("PageSize", 20);
			pageSize = pageSize < 0 ? 10 : pageSize;

			var totalPages = pagination.Get<int>("TotalPages", -1);
			totalPages = totalPages < 0
				? totalRecords > 0
					? Extensions.GetTotalPages(totalRecords, pageSize)
					: 0
				: totalPages;

			var pageNumber = pagination.Get<int>("PageNumber", 1);
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
		{
			return new JObject()
			{
				{ "TotalRecords", totalRecords },
				{ "TotalPages", totalPages},
				{ "PageSize", pageSize },
				{ "PageNumber", pageNumber }
			};
		}

		/// <summary>
		/// Gets the pagination JSON
		/// </summary>
		/// <param name="pagination"></param>
		/// <returns></returns>
		public static JObject GetPagination(this Tuple<long, int, int, int> pagination)
		{
			return Extensions.GetPagination(pagination.Item1, pagination.Item2, pagination.Item3, pagination.Item4);
		}
		#endregion

	}
}