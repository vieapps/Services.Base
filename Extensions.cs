#region Related components
using System;
using System.IO;
using System.Net;
using System.Web;
using System.Linq;
using System.Dynamic;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

using WampSharp.Core.Listener;
using WampSharp.V2;
using WampSharp.V2.Realm;
using WampSharp.V2.Core.Contracts;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using JSPool;
using JavaScriptEngineSwitcher.Core;
using JavaScriptEngineSwitcher.ChakraCore;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Helper extension methods for working with services
	/// </summary>
	public static partial class Extensions
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
				foreach (JObject childJson in children)
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

		#region Authentication & Authorization
		/// <summary>
		/// Gets the state that determines the user is authenticated or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <returns></returns>
		public static bool IsAuthenticated(this RequestInfo requestInfo)
			=> requestInfo != null && requestInfo.Session != null && requestInfo.Session.User != null && requestInfo.Session.User.IsAuthenticated;

		/// <summary>
		/// Gets the state that determines the user is system administrator or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<bool> IsSystemAdministratorAsync(this IUser user, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (user == null || !user.IsAuthenticated)
				return false;

			else
				try
				{
					var result = await new RequestInfo
					{
						Session = new Session() { User = new User(user) },
						ServiceName = "users",
						ObjectName = "account",
						Verb = "GET",
						Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
						{
							{ "IsSystemAdministrator", "" }
						},
						CorrelationID = correlationID ?? UtilityService.NewUUID
					}.CallServiceAsync(cancellationToken).ConfigureAwait(false);
					return user.ID.IsEquals(result.Get<string>("ID")) && result.Get<bool>("IsSystemAdministrator") == true;
				}
				catch
				{
					return false;
				}
		}

		/// <summary>
		/// Gets the state that determines the user is system administrator or not
		/// </summary>
		/// <param name="session">The session information</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static Task<bool> IsSystemAdministratorAsync(this Session session, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> session != null && session.User != null
				? session.User.IsSystemAdministratorAsync(correlationID, cancellationToken)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is system administrator or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static Task<bool> IsSystemAdministratorAsync(this RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
			=> requestInfo != null && requestInfo.Session != null
				? requestInfo.Session.IsSystemAdministratorAsync(requestInfo?.CorrelationID, cancellationToken)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<bool> IsServiceAdministratorAsync(this IUser user, string serviceName = null, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> user != null && user.IsAuthenticated
				? await user.IsSystemAdministratorAsync(correlationID, cancellationToken).ConfigureAwait(false) || user.IsAuthorized(serviceName, null, null, Components.Security.Action.Full, null, getPrivileges, getActions)
				: false;

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="session">The session information</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static Task<bool> IsServiceAdministratorAsync(this Session session, string serviceName = null, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> session != null && session.User != null
				? session.User.IsServiceAdministratorAsync(serviceName, getPrivileges, getActions, correlationID, cancellationToken)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information and related service</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static Task<bool> IsServiceAdministratorAsync(this RequestInfo requestInfo, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> requestInfo != null && requestInfo.Session != null
				? requestInfo.Session.IsServiceAdministratorAsync(requestInfo.ServiceName, getPrivileges, getActions, correlationID, cancellationToken)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<bool> IsServiceModeratorAsync(this IUser user, string serviceName = null, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> user != null && user.IsAuthenticated
				? await user.IsServiceAdministratorAsync(serviceName, getPrivileges, getActions, correlationID, cancellationToken).ConfigureAwait(false) || user.IsAuthorized(serviceName, null, null, Components.Security.Action.Approve, null, getPrivileges, getActions)
				: false;

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="session">The session information</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static Task<bool> IsServiceModeratorAsync(this Session session, string serviceName = null, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> session != null && session.User != null
				? session.User.IsServiceModeratorAsync(serviceName, getPrivileges, getActions, correlationID, cancellationToken)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information and related service</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static Task<bool> IsServiceModeratorAsync(this RequestInfo requestInfo, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> requestInfo != null && requestInfo.Session != null
				? requestInfo.Session.IsServiceModeratorAsync(requestInfo.ServiceName, getPrivileges, getActions, correlationID, cancellationToken)
				: Task.FromResult(false);

		/// <summary>
		/// The the global privilege role of the user
		/// </summary>
		/// <param name="user"></param>
		/// <param name="serviceName"></param>
		/// <returns></returns>
		public static string GetPrivilegeRole(this IUser user, string serviceName)
		{
			var privilege = user != null && user.Privileges != null
				? user.Privileges.FirstOrDefault(p => p.ServiceName.IsEquals(serviceName) && string.IsNullOrWhiteSpace(p.ObjectName) && string.IsNullOrWhiteSpace(p.ObjectIdentity))
				: null;
			return privilege?.Role ?? PrivilegeRole.Viewer.ToString();
		}

		/// <summary>
		/// Gets the default privileges  of the user
		/// </summary>
		/// <param name="user"></param>
		/// <param name="privileges"></param>
		/// <returns></returns>
		public static List<Privilege> GetPrivileges(this IUser user, Privileges privileges, string serviceName) => null;

		/// <summary>
		/// Gets the default privilege actions
		/// </summary>
		/// <param name="role"></param>
		/// <returns></returns>
		public static List<string> GetPrivilegeActions(this PrivilegeRole role)
		{
			var actions = new List<Components.Security.Action>();
			switch (role)
			{
				case PrivilegeRole.Administrator:
					actions = new List<Components.Security.Action>()
					{
						Components.Security.Action.Full
					};
					break;

				case PrivilegeRole.Moderator:
					actions = new List<Components.Security.Action>()
					{
						Components.Security.Action.Approve,
						Components.Security.Action.Update,
						Components.Security.Action.Create,
						Components.Security.Action.View,
						Components.Security.Action.Download
					};
					break;

				case PrivilegeRole.Editor:
					actions = new List<Components.Security.Action>()
					{
						Components.Security.Action.Update,
						Components.Security.Action.Create,
						Components.Security.Action.View,
						Components.Security.Action.Download
					};
					break;

				case PrivilegeRole.Contributor:
					actions = new List<Components.Security.Action>()
					{
						Components.Security.Action.Create,
						Components.Security.Action.View,
						Components.Security.Action.Download
					};
					break;

				default:
					actions = new List<Components.Security.Action>()
					{
						Components.Security.Action.View,
						Components.Security.Action.Download
					};
					break;
			}
			return actions.Select(a => a.ToString()).ToList();
		}

		/// <summary>
		/// Gets the state that determines the user can perform the action or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="serviceName">The name of the service</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <param name="action">The action to perform on the object of this service</param>
		/// <param name="privileges">The working privileges of the object (entity)</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<bool> IsAuthorizedAsync(this IUser user, string serviceName, string objectName, string objectIdentity, Components.Security.Action action, Privileges privileges = null, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> await user.IsSystemAdministratorAsync(correlationID, cancellationToken).ConfigureAwait(false)
				? true
				: user != null
					? user.IsAuthorized(serviceName, objectName, objectIdentity, action, privileges, getPrivileges, getActions)
					: false;

		/// <summary>
		/// Gets the state that determines the user can perform the action or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <param name="action">The action to perform on the object of this service</param>
		/// <param name="privileges">The working privileges of the object (entity)</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static Task<bool> IsAuthorizedAsync(this RequestInfo requestInfo, Components.Security.Action action, Privileges privileges = null, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null, CancellationToken cancellationToken = default(CancellationToken))
			=> requestInfo.Session != null && requestInfo.Session.User != null
				? requestInfo.Session.User.IsAuthorizedAsync(requestInfo.ServiceName, requestInfo.ObjectName, requestInfo.GetObjectIdentity(true), action, privileges, getPrivileges, getActions, requestInfo.CorrelationID, cancellationToken)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user can perform the action or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <param name="entity">The business entity object</param>
		/// <param name="action">The action to perform on the object of this service</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<bool> IsAuthorizedAsync(this RequestInfo requestInfo, IBusinessEntity entity, Components.Security.Action action, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null, CancellationToken cancellationToken = default(CancellationToken))
			=> await requestInfo.IsSystemAdministratorAsync(cancellationToken).ConfigureAwait(false)
				? true
				: requestInfo != null && requestInfo.Session != null && requestInfo.Session.User != null
					? requestInfo.Session.User.IsAuthorized(requestInfo.ServiceName, requestInfo.ObjectName, entity?.ID, action, entity?.WorkingPrivileges, getPrivileges, getActions)
					: false;

		/// <summary>
		/// Gets the state that determines the user is able to manage or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="serviceName">The name of the service</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<bool> CanManageAsync(this IUser user, string serviceName, string objectName, string objectIdentity, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> await user.IsSystemAdministratorAsync(correlationID, cancellationToken).ConfigureAwait(false) || user.IsAuthorized(serviceName, objectName, objectIdentity, Components.Security.Action.Full, null, getPrivileges ?? ((usr, privileges) => usr.GetPrivileges(privileges, serviceName)), getActions ?? Extensions.GetPrivilegeActions);

		/// <summary>
		/// Gets the state that determines the user is able to manage or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="serviceName">The name of the service</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<bool> CanManageAsync(this IUser user, string serviceName, string systemID, string definitionID, string objectID, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			// check user
			if (string.IsNullOrWhiteSpace(user?.ID))
				return false;

			// system administrator can do anything
			if (await user.IsSystemAdministratorAsync(correlationID, cancellationToken).ConfigureAwait(false))
				return true;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID, cancellationToken).ConfigureAwait(false);

			// get the permissions state
			return @object != null && @object is IBusinessEntity
				? user.IsAuthorized(serviceName, @object.GetType().GetTypeName(true), objectID, Components.Security.Action.Full, (@object as IBusinessEntity).WorkingPrivileges, getPrivileges ?? ((usr, privileges) => usr.GetPrivileges(privileges, serviceName)), getActions ?? Extensions.GetPrivilegeActions)
				: false;
		}

		/// <summary>
		/// Gets the state that determines the user is able to moderate or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="serviceName">The name of the service</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<bool> CanModerateAsync(this IUser user, string serviceName, string objectName, string objectIdentity, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> user != null && await user.CanManageAsync(serviceName, objectName, objectIdentity, getPrivileges, getActions, correlationID, cancellationToken).ConfigureAwait(false)
				? true
				: user != null && user.IsAuthorized(serviceName, objectName, objectIdentity, Components.Security.Action.Approve, null, getPrivileges ?? ((usr, privileges) => usr.GetPrivileges(privileges, serviceName)), getActions ?? Extensions.GetPrivilegeActions);

		/// <summary>
		/// Gets the state that determines the user is able to moderate or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="serviceName">The name of the service</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<bool> CanModerateAsync(this IUser user, string serviceName, string systemID, string definitionID, string objectID, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			// administrator can do
			if (user != null && await user.CanManageAsync(serviceName, systemID, definitionID, objectID, getPrivileges, getActions, correlationID, cancellationToken).ConfigureAwait(false))
				return true;

			// check user
			if (string.IsNullOrWhiteSpace(user?.ID))
				return false;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID, cancellationToken).ConfigureAwait(false);

			// get the permissions state
			return @object != null && @object is IBusinessEntity
				? user.IsAuthorized(serviceName, @object.GetType().GetTypeName(true), objectID, Components.Security.Action.Approve, (@object as IBusinessEntity).WorkingPrivileges, getPrivileges ?? ((usr, privileges) => usr.GetPrivileges(privileges, serviceName)), getActions ?? Extensions.GetPrivilegeActions)
				: false;
		}

		/// <summary>
		/// Gets the state that determines the user is able to edit or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="serviceName">The name of the service</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<bool> CanEditAsync(this IUser user, string serviceName, string objectName, string objectIdentity, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> user != null && await user.CanModerateAsync(serviceName, objectName, objectIdentity, getPrivileges, getActions, correlationID, cancellationToken).ConfigureAwait(false)
				? true
				: user != null && user.IsAuthorized(serviceName, objectName, objectIdentity, Components.Security.Action.Update, null, getPrivileges ?? ((usr, privileges) => usr.GetPrivileges(privileges, serviceName)), getActions ?? Extensions.GetPrivilegeActions);

		/// <summary>
		/// Gets the state that determines the user is able to edit or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="serviceName">The name of the service</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<bool> CanEditAsync(this IUser user, string serviceName, string systemID, string definitionID, string objectID, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			// moderator can do
			if (user != null && await user.CanModerateAsync(serviceName, systemID, definitionID, objectID, getPrivileges, getActions, correlationID, cancellationToken).ConfigureAwait(false))
				return true;

			// check user
			if (string.IsNullOrWhiteSpace(user?.ID))
				return false;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID, cancellationToken).ConfigureAwait(false);

			// get the permissions state
			return @object != null && @object is IBusinessEntity
				? user.IsAuthorized(serviceName, @object.GetType().GetTypeName(true), objectID, Components.Security.Action.Update, (@object as IBusinessEntity).WorkingPrivileges, getPrivileges ?? ((usr, privileges) => usr.GetPrivileges(privileges, serviceName)), getActions ?? Extensions.GetPrivilegeActions)
				: false;
		}

		/// <summary>
		/// Gets the state that determines the user is able to contribute or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="serviceName">The name of the service</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<bool> CanContributeAsync(this IUser user, string serviceName, string objectName, string objectIdentity, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> user != null && await user.CanEditAsync(serviceName, objectName, objectIdentity, getPrivileges, getActions, correlationID, cancellationToken).ConfigureAwait(false)
				? true
				: user != null && user.IsAuthorized(serviceName, objectName, objectIdentity, Components.Security.Action.Create, null, getPrivileges ?? ((usr, privileges) => usr.GetPrivileges(privileges, serviceName)), getActions ?? Extensions.GetPrivilegeActions);

		/// <summary>
		/// Gets the state that determines the user is able to contribute or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="serviceName">The name of the service</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<bool> CanContributeAsync(this IUser user, string serviceName, string systemID, string definitionID, string objectID, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			// editor can do
			if (user != null && await user.CanEditAsync(serviceName, systemID, definitionID, objectID, getPrivileges, getActions, correlationID, cancellationToken).ConfigureAwait(false))
				return true;

			// check user
			if (string.IsNullOrWhiteSpace(user?.ID))
				return false;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID, cancellationToken).ConfigureAwait(false);

			// get the permissions state
			return @object != null && @object is IBusinessEntity
				? user.IsAuthorized(serviceName, @object.GetType().GetTypeName(true), objectID, Components.Security.Action.Create, (@object as IBusinessEntity).WorkingPrivileges, getPrivileges ?? ((usr, privileges) => usr.GetPrivileges(privileges, serviceName)), getActions ?? Extensions.GetPrivilegeActions)
				: false;
		}

		/// <summary>
		/// Gets the state that determines the user is able to view or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="serviceName">The name of the service</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<bool> CanViewAsync(this IUser user, string serviceName, string objectName, string objectIdentity, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> user != null && await user.CanContributeAsync(serviceName, objectName, objectIdentity, getPrivileges, getActions, correlationID, cancellationToken).ConfigureAwait(false)
				? true
				: user != null && user.IsAuthorized(serviceName, objectName, objectIdentity, Components.Security.Action.View, null, getPrivileges ?? ((usr, privileges) => usr.GetPrivileges(privileges, serviceName)), getActions ?? Extensions.GetPrivilegeActions);

		/// <summary>
		/// Gets the state that determines the user is able to view or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="serviceName">The name of the service</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<bool> CanViewAsync(this IUser user, string serviceName, string systemID, string definitionID, string objectID, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			// contributor can do
			if (user != null && await user.CanContributeAsync(serviceName, systemID, definitionID, objectID, getPrivileges, getActions).ConfigureAwait(false))
				return true;

			// check user
			if (string.IsNullOrWhiteSpace(user?.ID))
				return false;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID, cancellationToken).ConfigureAwait(false);

			// get the permissions state
			return @object != null && @object is IBusinessEntity
				? user.IsAuthorized(serviceName, @object.GetType().GetTypeName(true), objectID, Components.Security.Action.View, (@object as IBusinessEntity).WorkingPrivileges, getPrivileges ?? ((usr, privileges) => usr.GetPrivileges(privileges, serviceName)), getActions ?? Extensions.GetPrivilegeActions)
				: false;
		}

		/// <summary>
		/// Gets the state that determines the user is able to download or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="serviceName">The name of the service</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<bool> CanDownloadAsync(this IUser user, string serviceName, string objectName, string objectIdentity, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> user != null && await user.CanModerateAsync(serviceName, objectName, objectIdentity, getPrivileges, getActions, correlationID, cancellationToken).ConfigureAwait(false)
				? true
				: user != null && user.IsAuthorized(serviceName, objectName, objectIdentity, Components.Security.Action.Download, null, getPrivileges ?? ((usr, privileges) => usr.GetPrivileges(privileges, serviceName)), getActions ?? Extensions.GetPrivilegeActions);

		/// <summary>
		/// Gets the state that determines the user is able to download or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="serviceName">The name of the service</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<bool> CanDownloadAsync(this IUser user, string serviceName, string systemID, string definitionID, string objectID, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			// moderator can do
			if (user != null && await user.CanModerateAsync(serviceName, systemID, definitionID, objectID, getPrivileges, getActions, correlationID, cancellationToken).ConfigureAwait(false))
				return true;

			// check user
			if (string.IsNullOrWhiteSpace(user?.ID))
				return false;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID, cancellationToken).ConfigureAwait(false);

			// get the permissions state
			return @object != null && @object is IBusinessEntity
				? user.IsAuthorized(serviceName, @object.GetType().GetTypeName(true), objectID, Components.Security.Action.Download, (@object as IBusinessEntity).WorkingPrivileges, getPrivileges ?? ((usr, privileges) => usr.GetPrivileges(privileges, serviceName)), getActions ?? Extensions.GetPrivilegeActions)
				: false;
		}
		#endregion

		#region Exceptions
		/// <summary>
		/// Gets the stack trace of this error exception
		/// </summary>
		/// <param name="exception"></param>
		/// <returns></returns>
		public static string GetStack(this Exception exception)
		{
			var stack = "";
			if (exception != null && exception is WampException)
			{
				var details = (exception as WampException).GetDetails();
				stack = details.Item4?.Replace("\\r", "\r")?.Replace("\\n", "\n")?.Replace(@"\\", @"\");
				if (details.Item6 != null)
					stack = details.Item6.ToString(Formatting.Indented).Replace("\\r", "\r").Replace("\\n", "\n").Replace(@"\\", @"\");
			}
			else if (exception != null)
			{
				stack = exception.StackTrace;
				var inner = exception.InnerException;
				var counter = 0;
				while (inner != null)
				{
					counter++;
					stack += "\r\n" + $"--- Inner ({counter}): ---------------------- " + "\r\n"
						+ "> Message: " + inner.Message + "\r\n"
						+ "> Type: " + inner.GetType().ToString() + "\r\n"
						+ inner.StackTrace;
					inner = inner.InnerException;
				}
			}
			return stack;
		}

		/// <summary>
		/// Gets the details of a WAMP exception
		/// </summary>
		/// <param name="exception"></param>
		/// <param name="requestInfo"></param>
		/// <returns></returns>
		public static Tuple<int, string, string, string, Exception, JObject> GetDetails(this WampException exception, RequestInfo requestInfo = null)
		{
			var code = 500;
			var message = "";
			var type = "";
			var stack = "";
			Exception inner = null;
			JObject jsonException = null;

			// unavailable
			if (exception.ErrorUri.Equals("wamp.error.no_such_procedure") || exception.ErrorUri.Equals("wamp.error.callee_unregistered"))
			{
				if (exception.Arguments != null && exception.Arguments.Length > 0 && exception.Arguments[0] != null && exception.Arguments[0] is JValue)
				{
					message = (exception.Arguments[0] as JValue).Value.ToString();
					var start = message.IndexOf("'");
					var end = message.IndexOf("'", start + 1);
					message = $"The requested service ({message.Substring(start + 1, end - start - 1).Replace("'", "")}) is unavailable";
				}
				else
					message = "The requested service is unavailable";

				type = "ServiceUnavailableException";
				stack = exception.StackTrace;
				code = 503;
			}

			// cannot serialize
			else if (exception.ErrorUri.Equals("wamp.error.invalid_argument"))
			{
				message = "Cannot serialize or deserialize one of argument objects (or child object)";
				if (exception.Arguments != null && exception.Arguments.Length > 0 && exception.Arguments[0] != null && exception.Arguments[0] is JValue)
					message += $" => {(exception.Arguments[0] as JValue).Value}";
				type = "SerializationException";
				stack = exception.StackTrace;
			}

			// runtime error
			else if (exception.ErrorUri.Equals("wamp.error.runtime_error"))
			{
				if (exception.Arguments != null && exception.Arguments.Length > 0 && exception.Arguments[0] != null && exception.Arguments[0] is JObject)
					foreach (var info in exception.Arguments[0] as JObject)
					{
						if (info.Value != null && info.Value is JValue && (info.Value as JValue).Value != null)
							stack += (stack.Equals("") ? "" : "\r\n" + $"----- Inner ({info.Key}) --------------------" + "\r\n")
								+ (info.Value as JValue).Value.ToString();
					}

				if (requestInfo == null && exception.Arguments != null && exception.Arguments.Length > 2 && exception.Arguments[2] != null && exception.Arguments[2] is JObject)
				{
					var info = (exception.Arguments[2] as JObject).First;
					if (info != null && info is JProperty && (info as JProperty).Name.Equals("RequestInfo") && (info as JProperty).Value != null && (info as JProperty).Value is JObject)
						requestInfo = ((info as JProperty).Value as JToken).FromJson<RequestInfo>();
				}

				jsonException = exception.Arguments != null && exception.Arguments.Length > 4 && exception.Arguments[4] != null && exception.Arguments[4] is JObject
					? Extensions.GetJsonException(exception.Arguments[4] as JObject)
					: null;

				message = jsonException != null
					? (jsonException["Message"] as JValue).Value.ToString()
					: $"Error occurred at \"net.vieapps.services.{(requestInfo != null ? requestInfo.ServiceName.ToLower() : "unknown")}\"";

				type = jsonException != null
					? (jsonException["Type"] as JValue).Value.ToString().ToArray('.').Last()
					: "ServiceOperationException";

				inner = exception;
			}

			// unknown
			else
			{
				message = exception.Message;
				type = exception.GetType().GetTypeName(true);
				stack = exception.StackTrace;
				inner = exception.InnerException;
			}

			return new Tuple<int, string, string, string, Exception, JObject>(code, message, type, stack, inner, jsonException);
		}

		static JObject GetJsonException(JObject exception)
		{
			var json = new JObject
			{
				{ "Message", exception["Message"] },
				{ "Type", exception["ClassName"] },
				{ "Method", exception["ExceptionMethod"] },
				{ "Source", exception["Source"] },
				{ "Stack", exception["StackTraceString"] },
			};

			var inner = exception["InnerException"];
			if (inner != null && inner is JObject)
				json.Add(new JProperty("InnerException", Extensions.GetJsonException(inner as JObject)));

			return json;
		}
		#endregion

		#region Location
		/// <summary>
		/// Gets the current location (IP-based)
		/// </summary>
		public static string CurrentLocation { get; private set; } = "Unknown";

		/// <summary>
		/// Gets the location of the session (IP-based)
		/// </summary>
		/// <param name="session"></param>
		/// <param name="correlationID"></param>
		/// <returns></returns>
		public static async Task<string> GetLocationAsync(this Session session, string correlationID = null)
		{
			correlationID = correlationID ?? UtilityService.NewUUID;
			try
			{
				var json = await WAMPConnections.CallServiceAsync(new RequestInfo(session, "IPLocations", "IP-Location")
				{
					Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
					{
						{ "ip-address", session.IP }
					},
					CorrelationID = correlationID
				}).ConfigureAwait(false);

				var city = json.Get<string>("City");
				var region = json.Get<string>("Region");
				if (region.Equals(city) && !"N/A".IsEquals(city))
					region = "";
				var country = json.Get<string>("Country");

				if ("N/A".IsEquals(city) && "N/A".IsEquals(region) && "N/A".IsEquals(country))
				{
					if ("Unknown".IsEquals(Extensions.CurrentLocation))
					{
						json = await WAMPConnections.CallServiceAsync(new RequestInfo(session, "IPLocations", "Current")
						{
							CorrelationID = correlationID
						}).ConfigureAwait(false);
						city = json.Get<string>("City");
						region = json.Get<string>("Region");
						if (region.Equals(city) && !"N/A".IsEquals(city))
							region = "";
						country = json.Get<string>("Country");
						Extensions.CurrentLocation = $"{city}, {region}, {country}".Replace(", ,", ",");
					}
					return Extensions.CurrentLocation;
				}

				return $"{city}, {region}, {country}".Replace(", ,", ",");
			}
			catch
			{
				return "Unknown";
			}
		}

		/// <summary>
		/// Gets the location of the request (IP-based)
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <returns></returns>
		public static Task<string> GetLocationAsync(this RequestInfo requestInfo)
			=> requestInfo.Session?.GetLocationAsync(requestInfo.CorrelationID) ?? Task.FromResult("Unknown");
		#endregion

		#region Unique name
		/// <summary>
		/// Gets the unique name of a business service
		/// </summary>
		/// <param name="name">The string that presents name of a business service</param>
		/// <param name="user">The string that presents name of the user who runs the process of the business service</param>
		/// <param name="host">The string that presents name of the host that runs the process of the business service</param>
		/// <param name="platform">The string that presents name of the platform that runs the process of the business service</param>
		/// <param name="os">The string that presents name of the operating system that runs the process of the business service</param>
		/// <returns>The string that presents unique name of a business service at a host</returns>
		public static string GetUniqueName(string name, string user, string host, string platform, string os)
		{
			name = (name ?? "unknown").Trim().ToLower();
			user = (user ?? Environment.UserName).Trim().ToLower();
			host = (host ?? Environment.MachineName).Trim().ToLower();
			platform = (platform ?? RuntimeInformation.FrameworkDescription).Trim();
			os = (os ?? $"{(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux" : "macOS")} {RuntimeInformation.OSArchitecture} ({(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "Macintosh; Intel Mac OS X; " : "")}{RuntimeInformation.OSDescription.Trim()})").Trim();
			return $"{name}.{user}-{host}-" + $"{platform} @ {os}".GenerateUUID();
		}

		/// <summary>
		/// Gets the unique name of a business service
		/// </summary>
		/// <param name="name">The string that presents name of a business service</param>
		/// <param name="args">The starting arguments</param>
		/// <returns>The string that presents unique name of a business service at a host</returns>
		public static string GetUniqueName(string name, string[] args)
		{
			var user = args?.FirstOrDefault(a => a.IsStartsWith("/run-user:"));			
			var host = args?.FirstOrDefault(a => a.IsStartsWith("/run-host:"));
			var platform = args?.FirstOrDefault(a => a.IsStartsWith("/run-platform:"));
			var os = args?.FirstOrDefault(a => a.IsStartsWith("/run-os:"));
			return Extensions.GetUniqueName(name, user?.Replace(StringComparison.OrdinalIgnoreCase, "/run-user:", "").Trim().UrlDecode(), host?.Replace(StringComparison.OrdinalIgnoreCase, "/run-host:", "").Trim().UrlDecode(), platform?.Replace(StringComparison.OrdinalIgnoreCase, "/run-platform:", "").Trim().UrlDecode(), os?.Replace(StringComparison.OrdinalIgnoreCase, "/run-os:", "").Trim().UrlDecode());
		}
		#endregion

		#region Encryption
		/// <summary>
		/// Gest a key for encrypting/decrypting data with this session
		/// </summary>
		/// <param name="session"></param>
		/// <param name="seeds">The seeds for hashing</param>
		/// <param name="storage">The storage</param>
		/// <returns></returns>
		public static byte[] GetEncryptionKey(this Session session, byte[] seeds = null, IDictionary<object, object> storage = null)
			=> storage != null
				? storage.ContainsKey("EncryptionKey")
					? storage["EncryptionKey"] as byte[]
					: (storage["EncryptionKey"] = session.SessionID.GetHMACHash(seeds ?? CryptoService.DEFAULT_PASS_PHRASE.ToBytes(), "SHA512").GenerateHashKey(256)) as byte[]
				: session.SessionID.GetHMACHash(seeds ?? CryptoService.DEFAULT_PASS_PHRASE.ToBytes(), "SHA512").GenerateHashKey(256);

		/// <summary>
		/// Gest a key for encrypting/decrypting data with this session
		/// </summary>
		/// <param name="session"></param>
		/// <param name="seeds">The seeds for hashing</param>
		/// <param name="storage">The storage</param>
		/// <returns></returns>
		public static byte[] GetEncryptionKey(this Session session, string seeds, IDictionary<object, object> storage)
			=> session.GetEncryptionKey((seeds ?? CryptoService.DEFAULT_PASS_PHRASE).ToBytes(), storage);

		/// <summary>
		/// Gest an initialize vector for encrypting/decrypting data with this session
		/// </summary>
		/// <param name="session"></param>
		/// <param name="seeds">The seeds for hashing</param>
		/// <param name="storage">The storage</param>
		/// <returns></returns>
		public static byte[] GetEncryptionIV(this Session session, byte[] seeds = null, IDictionary<object, object> storage = null)
			=> storage != null
				? storage.ContainsKey("EncryptionIV")
					? storage["EncryptionIV"] as byte[]
					: (storage["EncryptionIV"] = session.SessionID.GetHMACHash(seeds ?? CryptoService.DEFAULT_PASS_PHRASE.ToBytes(), "SHA256").GenerateHashKey(128)) as byte[]
				: session.SessionID.GetHMACHash(seeds ?? CryptoService.DEFAULT_PASS_PHRASE.ToBytes(), "SHA256").GenerateHashKey(128);

		/// <summary>
		/// Gest an initialize vector for encrypting/decrypting data with this session
		/// </summary>
		/// <param name="session"></param>
		/// <param name="seeds">The seeds for hashing</param>
		/// <param name="storage">The storage</param>
		/// <returns></returns>
		public static byte[] GetEncryptionIV(this Session session, string seeds, IDictionary<object, object> storage)
			=> session.GetEncryptionIV((seeds ?? CryptoService.DEFAULT_PASS_PHRASE).ToBytes(), storage);

		/// <summary>
		/// Encrypts the identity (hexa-string)
		/// </summary>
		/// <param name="session"></param>
		/// <param name="id">The identity (hexa-string)</param>
		/// <param name="keySeeds">The seeds for generating key</param>
		/// <param name="ivSeeds">The seeds for generating initialize vector</param>
		/// <returns></returns>
		public static string GetEncryptedID(this Session session, string id, string keySeeds = null, string ivSeeds = null)
			=> !string.IsNullOrWhiteSpace(id)
				? id.HexToBytes().Encrypt(session.GetEncryptionKey(keySeeds ?? CryptoService.DEFAULT_PASS_PHRASE, null), session.GetEncryptionIV(ivSeeds ?? CryptoService.DEFAULT_PASS_PHRASE, null)).ToHex()
				: null;

		/// <summary>
		/// Decrypts the identity (hexa-string)
		/// </summary>
		/// <param name="session"></param>
		/// <param name="id">The identity (hexa-string)</param>
		/// <param name="keySeeds">The seeds for generating key</param>
		/// <param name="ivSeeds">The seeds for generating initialize vector</param>
		/// <returns></returns>
		public static string GetDecryptedID(this Session session, string id, string keySeeds = null, string ivSeeds = null)
			=> !string.IsNullOrWhiteSpace(id)
				? id.HexToBytes().Decrypt(session.GetEncryptionKey(keySeeds ?? CryptoService.DEFAULT_PASS_PHRASE, null), session.GetEncryptionIV(ivSeeds ?? CryptoService.DEFAULT_PASS_PHRASE, null)).ToHex()
				: null;
		#endregion

		#region Evaluate Javascript expression
		static Extensions()
		{
			JsEngineSwitcher.Current.DefaultEngineName = ChakraCoreJsEngine.EngineName;
			JsEngineSwitcher.Current.EngineFactories.AddChakraCore(new ChakraCoreSettings
			{
				DisableEval = true,
				EnableExperimentalFeatures = true
			});

			Extensions.JsEnginePool = new JsPool(new JsPoolConfig
			{
				MaxEngines = UtilityService.GetAppSetting("JsEngine:MaxEngines", "25").CastAs<int>(),
				MaxUsagesPerEngine = UtilityService.GetAppSetting("JsEngine:MaxUsagesPerEngine", "100").CastAs<int>(),
				GetEngineTimeout = TimeSpan.FromSeconds(UtilityService.GetAppSetting("JsEngine:GetEngineTimeout", "3").CastAs<int>())
			});

			Extensions.JsFunctions = @"
			function __toDateTime(value) {
				if (value !== undefined) {
					if (value instanceof Date || (typeof value === 'string' && value.trim() !== '')) {
						var date = new Date(value);
						return new DateTime(date.getFullYear(), date.getMonth(), date.getDate(), date.getHours(), date.getMinutes(), date.getSeconds(), date.getMilliseconds());
					}
					else if (typeof value === 'number') {
						return new DateTime(value);
					}
					else {
						return new DateTime();
					}
				}
				else {
					return new DateTime();
				}
			}
			function __now() {
				return new Date().toJSON().replace('T', ' ').replace('Z', '').replace(/\-/g, '/');
			}
			function __today() {
				var date = new Date().toJSON();
				return date.substr(0, date.indexOf('T')).replace(/\-/g, '/');
			}
			function __getAnsiUri(value, lowerCase) {
				return value === undefined || typeof value !== 'string' || value.trim() === '' ? '' : __GetAnsiUri(value, lowerCase !== undefined ? lowerCase : true);
			}
			".Replace("\t", "").Replace("\r", "").Replace("\n", " ");
		}

		static JsPool JsEnginePool { get; }

		/// <summary>
		/// Gets the common Javascript functions
		/// </summary>
		public static string JsFunctions { get; }

		/// <summary>
		/// Casts the returning value of an Javascript expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="jsValue"></param>
		/// <returns></returns>
		public static T JsCast<T>(object jsValue)
			=> jsValue == null || jsValue is Undefined
				? default(T)
				: jsValue is string && typeof(T).Equals(typeof(DateTime)) && (jsValue as string).Contains("T") && (jsValue as string).Contains("Z") && DateTime.TryParse(jsValue as string, out DateTime datetime)
					? datetime.CastAs<T>()
					: jsValue.CastAs<T>();

		/// <summary>
		/// Prepare the Javascript engine
		/// </summary>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns></returns>
		public static IJsEngine PrepareJsEngine(this IJsEngine jsEngine, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
		{
			Func<DateTime> fn_Now = () => DateTime.Now;
			Func<string, bool, string> fn_GetAnsiUri = (name, lowerCase) => name.GetANSIUri(lowerCase);

			new Dictionary<string, object>(embedObjects ?? new Dictionary<string, object>())
			{
				["__Now"] = fn_Now,
				["__GetAnsiUri"] = fn_GetAnsiUri,
			}.ForEach(kvp => jsEngine.EmbedHostObject(kvp.Key, kvp.Value));

			new Dictionary<string, Type>(embedTypes ?? new Dictionary<string, Type>())
			{
				["Uri"] = typeof(Uri),
				["DateTime"] = typeof(DateTime),
			}.ForEach(kvp => jsEngine.EmbedHostType(kvp.Key, kvp.Value));
			return jsEngine;
		}

		/// <summary>
		/// Creates an Javascript engine
		/// </summary>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns></returns>
		public static IJsEngine CreateJsEngine(IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
			=> JsEngineSwitcher.Current.CreateDefaultEngine().PrepareJsEngine(embedObjects, embedTypes);

		/// <summary>
		/// Evaluates an Javascript expression
		/// </summary>
		/// <param name="jsEngine">The Javascript engine for evaluating an expression</param>
		/// <param name="expression">The string that presents an Javascript expression for evaluating</param>
		/// <returns>The object that presents returning value from .NET objects or Javascript object (only supported and converted to Undefied, Boolean, Int, Double and String)</returns>
		public static object JsEvaluate(this IJsEngine jsEngine, string expression)
		{
			var jsValue = jsEngine.Evaluate(expression);
			return jsValue != null && jsValue is Undefined
				? null
				: jsValue;
		}

		/// <summary>
		/// Evaluates an Javascript expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="jsEngine">The Javascript engine for evaluating an expression</param>
		/// <param name="expression">The string that presents an Javascript expression for evaluating</param>
		/// <returns>The object that presents returning value from .NET objects or Javascript object</returns>
		public static T JsEvaluate<T>(this IJsEngine jsEngine, string expression)
			=> Extensions.JsCast<T>(jsEngine.JsEvaluate(expression));

		/// <summary>
		/// Prepare the Javascript engine
		/// </summary>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns></returns>
		public static PooledJsEngine PrepareJsEngine(this PooledJsEngine jsEngine, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
		{
			jsEngine.InnerEngine.PrepareJsEngine(embedObjects, embedTypes);
			return jsEngine;
		}

		/// <summary>
		/// Gets an Javascript engine
		/// </summary>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns></returns>
		public static PooledJsEngine GetJsEngine(IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
			=> Extensions.JsEnginePool.GetEngine().PrepareJsEngine(embedObjects, embedTypes);

		/// <summary>
		/// Evaluates an Javascript expression
		/// </summary>
		/// <param name="jsEngine">The Javascript engine for evaluating an expression</param>
		/// <param name="expression">The string that presents an Javascript expression for evaluating</param>
		/// <returns>The object that presents returning value from .NET objects or Javascript object (only supported and converted to Undefied, Boolean, Int, Double and String)</returns>
		public static object JsEvaluate(this PooledJsEngine jsEngine, string expression)
			=> jsEngine.InnerEngine.JsEvaluate(expression);

		/// <summary>
		/// Evaluates an Javascript expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="jsEngine">The Javascript engine for evaluating an expression</param>
		/// <param name="expression">The string that presents an Javascript expression for evaluating</param>
		/// <returns>The object that presents returning value from .NET objects or Javascript object</returns>
		public static T JsEvaluate<T>(this PooledJsEngine jsEngine, string expression)
			=> jsEngine.InnerEngine.JsEvaluate<T>(expression);

		/// <summary>
		/// Evaluates an Javascript expression
		/// </summary>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns>The object that presents returning value from .NET objects or Javascript object (only supported and converted to Undefied, Boolean, Int, Double and String)</returns>
		public static object JsEvaluate(string expression, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
		{
			using (var jsEngine = Extensions.GetJsEngine(embedObjects, embedTypes))
			{
				return jsEngine.JsEvaluate(expression);
			}
		}

		/// <summary>
		/// Evaluates an Javascript expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns>The object that presents returning value from .NET objects or Javascript object</returns>
		public static T JsEvaluate<T>(string expression, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
		{
			using (var jsEngine = Extensions.GetJsEngine(embedObjects, embedTypes))
			{
				return jsEngine.JsEvaluate<T>(expression);
			}
		}

		/// <summary>
		/// Evaluates the collection of Javascript expressions
		/// </summary>
		/// <param name="expressions">The collection of Javascript expression for evaluating, each expression must end by statement 'return ..;' to return a value</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns>The collection of value that evaluated by the expressions</returns>
		public static IEnumerable<object> JsEvaluate(IEnumerable<string> expressions, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
		{
			using (var jsEngine = Extensions.GetJsEngine(embedObjects, embedTypes))
			{
				return expressions.Select(expression => jsEngine.JsEvaluate(expression)).ToList();
			}
		}
		#endregion

	}
}