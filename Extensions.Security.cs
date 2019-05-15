#region Related components
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

using net.vieapps.Components.Security;
using net.vieapps.Components.Repository;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Extension methods for working with services in the VIEApps NGX
	/// </summary>
	public static partial class Extensions
	{
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
						Session = new Session
						{
							User = new User(user)
						},
						ServiceName = "users",
						ObjectName = "account",
						Verb = "GET",
						Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
						{
							{ "IsSystemAdministrator", "" }
						},
						CorrelationID = correlationID ?? UtilityService.NewUUID
					}.CallServiceAsync(cancellationToken).ConfigureAwait(false);
					return user.ID.IsEquals(result.Get<string>("ID")) && result.Get<bool>("IsSystemAdministrator");
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
				? user.IsAuthorized(serviceName, @object.GetTypeName(true), objectID, Components.Security.Action.Full, (@object as IBusinessEntity).WorkingPrivileges, getPrivileges ?? ((usr, privileges) => usr.GetPrivileges(privileges, serviceName)), getActions ?? Extensions.GetPrivilegeActions)
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
				? user.IsAuthorized(serviceName, @object.GetTypeName(true), objectID, Components.Security.Action.Approve, (@object as IBusinessEntity).WorkingPrivileges, getPrivileges ?? ((usr, privileges) => usr.GetPrivileges(privileges, serviceName)), getActions ?? Extensions.GetPrivilegeActions)
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
				? user.IsAuthorized(serviceName, @object.GetTypeName(true), objectID, Components.Security.Action.Update, (@object as IBusinessEntity).WorkingPrivileges, getPrivileges ?? ((usr, privileges) => usr.GetPrivileges(privileges, serviceName)), getActions ?? Extensions.GetPrivilegeActions)
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
				? user.IsAuthorized(serviceName, @object.GetTypeName(true), objectID, Components.Security.Action.Create, (@object as IBusinessEntity).WorkingPrivileges, getPrivileges ?? ((usr, privileges) => usr.GetPrivileges(privileges, serviceName)), getActions ?? Extensions.GetPrivilegeActions)
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
				? user.IsAuthorized(serviceName, @object.GetTypeName(true), objectID, Components.Security.Action.View, (@object as IBusinessEntity).WorkingPrivileges, getPrivileges ?? ((usr, privileges) => usr.GetPrivileges(privileges, serviceName)), getActions ?? Extensions.GetPrivilegeActions)
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
				? user.IsAuthorized(serviceName, @object.GetTypeName(true), objectID, Components.Security.Action.Download, (@object as IBusinessEntity).WorkingPrivileges, getPrivileges ?? ((usr, privileges) => usr.GetPrivileges(privileges, serviceName)), getActions ?? Extensions.GetPrivilegeActions)
				: false;
		}
	}
}