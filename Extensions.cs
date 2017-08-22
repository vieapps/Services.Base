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

		#region Http errors
		/// <summary>
		/// Gets the approriate HTTP Status Code of the exception
		/// </summary>
		/// <param name="exception"></param>
		/// <returns></returns>
		public static int GetHttpStatusCode(this Exception exception)
		{
			if (exception is FileNotFoundException || exception is ServiceNotFoundException || exception is InformationNotFoundException)
				return (int)HttpStatusCode.NotFound;

			if (exception is AccessDeniedException)
				return (int)HttpStatusCode.Forbidden;

			if (exception is UnauthorizedException)
				return (int)HttpStatusCode.Unauthorized;

			if (exception is MethodNotAllowedException)
				return (int)HttpStatusCode.MethodNotAllowed;

			if (exception is InvalidRequestException)
				return (int)HttpStatusCode.BadRequest;

			if (exception is NotImplementedException)
				return (int)HttpStatusCode.NotImplemented;

			if (exception is ConnectionTimeoutException)
				return (int)HttpStatusCode.RequestTimeout;

			return (int)HttpStatusCode.InternalServerError;
		}

		/// <summary>
		/// Show HTTP error
		/// </summary>
		/// <param name="context"></param>
		/// <param name="code"></param>
		/// <param name="message"></param>
		/// <param name="type"></param>
		/// <param name="correlationID"></param>
		/// <param name="stack"></param>
		/// <param name="showStack"></param>
		public static void ShowHttpError(this HttpContext context, int code, string message, string type, string correlationID = null, string stack = null, bool showStack = true)
		{
			code = code < 1 ? (int)HttpStatusCode.InternalServerError : code;

			context.Response.TrySkipIisCustomErrors = true;
			context.Response.StatusCode = code;
			context.Response.Cache.SetNoStore();
			context.Response.ContentType = "text/html";

			context.Response.ClearContent();
			context.Response.Output.Write("<!DOCTYPE html>\r\n");
			context.Response.Output.Write("<html xmlns=\"http://www.w3.org/1999/xhtml\">\r\n");
			context.Response.Output.Write("<head><title>Error " + code.ToString() + "</title></head>\r\n<body>\r\n");
			context.Response.Output.Write("<h1>HTTP " + code.ToString() + " - " + message.Replace("<", "&lt;").Replace(">", "&gt;") + "</h1>\r\n");
			context.Response.Output.Write("<hr/>\r\n");
			context.Response.Output.Write("<div>Type: " + type + (!string.IsNullOrWhiteSpace(correlationID) ? " - Correlation ID: " + correlationID : "") + "</div>\r\n");
			if (!string.IsNullOrWhiteSpace(stack) && showStack)
				context.Response.Output.Write("<div><br/>Stack:</div>\r\n<blockquote>" + stack.Replace("<", "&lt;").Replace(">", "&gt;").Replace("\n", "<br/>").Replace("\r", "").Replace("\t", "") + "</blockquote>\r\n");
			context.Response.Output.Write("</body>\r\n</html>");

			if (message.IsContains("potentially dangerous"))
				context.Response.End();
		}
		#endregion

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

		static FilterBys<T> GetFilterBys<T>(string @operator, JArray children) where T : class
		{
			var childFilters = new List<IFilterBy<T>>();
			foreach (JProperty info in children)
			{
				IFilterBy<T> filter = null;
				var name = info.Name;
				var value = info.Value;

				// child expressions
				if (value is JArray)
					filter = Extensions.GetFilterBys<T>(name, value as JArray);

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
			var filters = filterby["Or"] != null && filterby["Or"] is JArray
				? Filters<T>.Or()
				: Filters<T>.And();

			foreach (var info in filterby)
				if (!info.Key.Equals("") && !info.Key.IsEquals("Query"))
				{
					IFilterBy<T> filter = null;
					var name = info.Key;
					var value = info.Value;

					// child expressions
					if (value is JArray && (name.IsEquals("And") || name.IsEquals("Or")))
						filter = Extensions.GetFilterBys<T>(name, value as JArray);

					// special comparisions
					else if (value is JValue)
					{
						var op = (value as JValue).Value.ToString();
						if (op.IsEquals("IsNull") || op.IsEquals("IsNotNull") || op.IsEquals("IsEmpty") || op.IsEquals("IsNotEmpty"))
							filter = Extensions.GetFilterBy<T>(name, op, null);
					}

					// norma comparisions
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
					: new JObject() {
							{
								@operator,
								new JValue(serverJson["Value"] != null ? (serverJson["Value"] as JValue).Value : null)
							}
						} as JToken;
				return new JProperty((serverJson["Attribute"] as JValue).Value.ToString(), token);
			}
			else
			{
				var clientJson = new JObject();
				foreach(JObject childJson in serverJson["Children"] as JArray)
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
			clientJson.Add(Extensions.AddClientJson(filterby.ToJson()));
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
			var totalRecords = pagination.Has("TotalRecords")
				? pagination.Get<long>("TotalRecords")
				: -1;

			var pageSize = pagination.Has("PageSize")
				? pagination.Get<int>("TotalRecords")
				: 20;
			if (pageSize < 0)
				pageSize = 20;

			var totalPages = pagination.Has("TotalPages")
				? pagination.Get<int>("TotalPages")
				: -1;
			if (totalPages < 0)
				totalPages = Extensions.GetTotalPages(totalRecords, pageSize);

			var pageNumber = pagination.Has("PageNumber")
				? pagination.Get<int>("PageNumber")
				: 20;
			if (pageNumber < 1)
				pageNumber = 1;
			else if (totalPages > 0 && pageNumber > totalPages)
				pageNumber = totalPages;

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