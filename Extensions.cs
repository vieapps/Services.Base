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

using WampSharp.Core.Listener;
using WampSharp.V2;
using WampSharp.V2.Realm;
using WampSharp.V2.Core.Contracts;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
		/// /// <param name="correlationID">The correlation identity</param>
		/// <returns></returns>
		public static async Task<bool> IsSystemAdministratorAsync(this IUser user, string correlationID = null)
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
					}.CallServiceAsync().ConfigureAwait(false);
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
		/// /// <param name="correlationID">The correlation identity</param>
		/// <returns></returns>
		public static Task<bool> IsSystemAdministratorAsync(this Session session, string correlationID = null)
			=> session != null && session.User != null
				? session.User.IsSystemAdministratorAsync(correlationID)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is system administrator or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <returns></returns>
		public static Task<bool> IsSystemAdministratorAsync(this RequestInfo requestInfo)
			=> requestInfo != null && requestInfo.Session != null
				? requestInfo.Session.IsSystemAdministratorAsync(requestInfo?.CorrelationID)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// /// <param name="serviceName">The name of service</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <returns></returns>
		public static async Task<bool> IsServiceAdministratorAsync(this IUser user, string serviceName = null, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
			=> user != null && user.IsAuthenticated
				? await user.IsSystemAdministratorAsync().ConfigureAwait(false) || user.IsAuthorized(serviceName, null, null, Components.Security.Action.Full, null, getPrivileges, getActions)
				: false;

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="session">The session information</param>
		/// /// <param name="serviceName">The name of service</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <returns></returns>
		public static Task<bool> IsServiceAdministratorAsync(this Session session, string serviceName = null, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
			=> session != null && session.User != null
				? session.User.IsServiceAdministratorAsync(serviceName, getPrivileges, getActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information and related service</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <returns></returns>
		public static Task<bool> IsServiceAdministratorAsync(this RequestInfo requestInfo, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
			=> requestInfo != null && requestInfo.Session != null
				? requestInfo.Session.IsServiceAdministratorAsync(requestInfo.ServiceName, getPrivileges, getActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// /// <param name="serviceName">The name of service</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <returns></returns>
		public static async Task<bool> IsServiceModeratorAsync(this IUser user, string serviceName = null, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
			=> user != null && user.IsAuthenticated
				? await user.IsServiceAdministratorAsync(serviceName).ConfigureAwait(false) || user.IsAuthorized(serviceName, null, null, Components.Security.Action.Approve, null, getPrivileges, getActions)
				: false;

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="session">The session information</param>
		/// /// <param name="serviceName">The name of service</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <returns></returns>
		public static Task<bool> IsServiceModeratorAsync(this Session session, string serviceName = null, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
			=> session != null && session.User != null
				? session.User.IsServiceModeratorAsync(serviceName, getPrivileges, getActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information and related service</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <returns></returns>
		public static Task<bool> IsServiceModeratorAsync(this RequestInfo requestInfo, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
			=> requestInfo != null && requestInfo.Session != null
				? requestInfo.Session.IsServiceModeratorAsync(requestInfo.ServiceName, getPrivileges, getActions)
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
		public static List<string> GetPrivilegeActions(PrivilegeRole role)
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
		/// <returns></returns>
		public static async Task<bool> IsAuthorizedAsync(this IUser user, string serviceName, string objectName, string objectIdentity, Components.Security.Action action, Privileges privileges = null, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
			=> await Extensions.IsSystemAdministratorAsync(user).ConfigureAwait(false)
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
		/// <returns></returns>
		public static Task<bool> IsAuthorizedAsync(this RequestInfo requestInfo, Components.Security.Action action, Privileges privileges = null, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
			=> requestInfo.Session != null && requestInfo.Session.User != null
				? requestInfo.Session.User.IsAuthorizedAsync(requestInfo.ServiceName, requestInfo.ObjectName, requestInfo.GetObjectIdentity(true), action, privileges, getPrivileges, getActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user can perform the action or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <param name="entity">The business entity object</param>
		/// <param name="action">The action to perform on the object of this service</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <returns></returns>
		public static async Task<bool> IsAuthorizedAsync(this RequestInfo requestInfo, IBusinessEntity entity, Components.Security.Action action, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
			=> await requestInfo.IsSystemAdministratorAsync().ConfigureAwait(false)
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
		/// <returns></returns>
		public static async Task<bool> CanManageAsync(this IUser user, string serviceName, string objectName, string objectIdentity, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
			=> await user.IsSystemAdministratorAsync().ConfigureAwait(false) || user.IsAuthorized(serviceName, objectName, objectIdentity, Components.Security.Action.Full, null, getPrivileges ?? ((usr, privileges) => usr.GetPrivileges(privileges, serviceName)), getActions ?? Extensions.GetPrivilegeActions);

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
		/// <returns></returns>
		public static async Task<bool> CanManageAsync(this IUser user, string serviceName, string systemID, string definitionID, string objectID, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
		{
			// check user
			if (string.IsNullOrWhiteSpace(user?.ID))
				return false;

			// system administrator can do anything
			if (await user.IsSystemAdministratorAsync().ConfigureAwait(false))
				return true;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID).ConfigureAwait(false);

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
		/// <returns></returns>
		public static async Task<bool> CanModerateAsync(this IUser user, string serviceName, string objectName, string objectIdentity, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
			=> user != null && await user.CanManageAsync(serviceName, objectName, objectIdentity, getPrivileges, getActions).ConfigureAwait(false)
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
		/// <returns></returns>
		public static async Task<bool> CanModerateAsync(this IUser user, string serviceName, string systemID, string definitionID, string objectID, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
		{
			// administrator can do
			if (user != null && await user.CanManageAsync(serviceName, systemID, definitionID, objectID, getPrivileges, getActions).ConfigureAwait(false))
				return true;

			// check user
			if (string.IsNullOrWhiteSpace(user?.ID))
				return false;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID).ConfigureAwait(false);

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
		/// <returns></returns>
		public static async Task<bool> CanEditAsync(this IUser user, string serviceName, string objectName, string objectIdentity, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
			=> user != null && await user.CanModerateAsync(serviceName, objectName, objectIdentity, getPrivileges, getActions).ConfigureAwait(false)
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
		/// <returns></returns>
		public static async Task<bool> CanEditAsync(this IUser user, string serviceName, string systemID, string definitionID, string objectID, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
		{
			// moderator can do
			if (user != null && await user.CanModerateAsync(serviceName, systemID, definitionID, objectID, getPrivileges, getActions).ConfigureAwait(false))
				return true;

			// check user
			if (string.IsNullOrWhiteSpace(user?.ID))
				return false;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID).ConfigureAwait(false);

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
		/// <returns></returns>
		public static async Task<bool> CanContributeAsync(this IUser user, string serviceName, string objectName, string objectIdentity, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
			=> user != null && await user.CanEditAsync(serviceName, objectName, objectIdentity, getPrivileges, getActions).ConfigureAwait(false)
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
		/// <returns></returns>
		public static async Task<bool> CanContributeAsync(this IUser user, string serviceName, string systemID, string definitionID, string objectID, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
		{
			// editor can do
			if (user != null && await user.CanEditAsync(serviceName, systemID, definitionID, objectID, getPrivileges, getActions).ConfigureAwait(false))
				return true;

			// check user
			if (string.IsNullOrWhiteSpace(user?.ID))
				return false;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID).ConfigureAwait(false);

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
		/// <returns></returns>
		public static async Task<bool> CanViewAsync(this IUser user, string serviceName, string objectName, string objectIdentity, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
			=> user != null && await user.CanContributeAsync(serviceName, objectName, objectIdentity, getPrivileges, getActions).ConfigureAwait(false)
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
		/// <returns></returns>
		public static async Task<bool> CanViewAsync(this IUser user, string serviceName, string systemID, string definitionID, string objectID, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
		{
			// contributor can do
			if (user != null && await user.CanContributeAsync(serviceName, systemID, definitionID, objectID, getPrivileges, getActions).ConfigureAwait(false))
				return true;

			// check user
			if (string.IsNullOrWhiteSpace(user?.ID))
				return false;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID).ConfigureAwait(false);

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
		/// <returns></returns>
		public static async Task<bool> CanDownloadAsync(this IUser user, string serviceName, string objectName, string objectIdentity, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
			=> user != null && await user.CanModerateAsync(serviceName, objectName, objectIdentity, getPrivileges, getActions).ConfigureAwait(false)
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
		/// <returns></returns>
		public static async Task<bool> CanDownloadAsync(this IUser user, string serviceName, string systemID, string definitionID, string objectID, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
		{
			// moderator can do
			if (user != null && await user.CanModerateAsync(serviceName, systemID, definitionID, objectID, getPrivileges, getActions).ConfigureAwait(false))
				return true;

			// check user
			if (string.IsNullOrWhiteSpace(user?.ID))
				return false;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID).ConfigureAwait(false);

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
		#endregion

	}
}