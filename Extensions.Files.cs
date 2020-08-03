#region Related components
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services
{
	public static partial class Extensions
	{
		/// <summary>
		/// Gets the collection of thumbnails
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="objectID"></param>
		/// <param name="objectTitle"></param>
		/// <param name="validationKey"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task<JToken> GetThumbnailsAsync(this RequestInfo requestInfo, string objectID = null, string objectTitle = null, string validationKey = null, CancellationToken cancellationToken = default, Action<string, Exception> tracker = null, Formatting jsonFormat = Formatting.None)
			=> requestInfo == null || requestInfo.Session == null
				? Task.FromResult<JToken>(null)
				: new RequestInfo(requestInfo.Session, "Files", "Thumbnail")
				{
					Header = requestInfo.Header,
					Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
					{
						["object-identity"] = "search",
						["x-object-id"] = objectID ?? requestInfo.GetObjectIdentity(),
						["x-object-title"] = objectTitle
					},
					Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
					{
						["Signature"] = (requestInfo.GetHeaderParameter("x-app-token") ?? "").GetHMACSHA256(validationKey ?? CryptoService.DEFAULT_PASS_PHRASE),
						["SessionID"] = requestInfo.Session.SessionID.GetHMACBLAKE256(validationKey ?? CryptoService.DEFAULT_PASS_PHRASE)
					},
					CorrelationID = requestInfo.CorrelationID
				}.CallServiceAsync(cancellationToken, null, null, null, tracker, jsonFormat);

		/// <summary>
		/// Gets the collection of attachments
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="objectID"></param>
		/// <param name="objectTitle"></param>
		/// <param name="validationKey"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task<JToken> GetAttachmentsAsync(this RequestInfo requestInfo, string objectID = null, string objectTitle = null, string validationKey = null, CancellationToken cancellationToken = default, Action<string, Exception> tracker = null, Formatting jsonFormat = Formatting.None)
			=> requestInfo == null || requestInfo.Session == null
				? Task.FromResult<JToken>(null)
				: new RequestInfo(requestInfo.Session, "Files", "Attachment")
				{
					Header = requestInfo.Header,
					Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
					{
						["object-identity"] = "search",
						["x-object-id"] = objectID ?? requestInfo.GetObjectIdentity(),
						["x-object-title"] = objectTitle
					},
					Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
					{
						["Signature"] = (requestInfo.GetHeaderParameter("x-app-token") ?? "").GetHMACSHA256(validationKey ?? CryptoService.DEFAULT_PASS_PHRASE),
						["SessionID"] = requestInfo.Session.SessionID.GetHMACBLAKE256(validationKey ?? CryptoService.DEFAULT_PASS_PHRASE)
					},
					CorrelationID = requestInfo.CorrelationID
				}.CallServiceAsync(cancellationToken, null, null, null, tracker, jsonFormat);

		/// <summary>
		/// Gets the collection of files (thumbnails and attachment files are included)
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="objectID"></param>
		/// <param name="objectTitle"></param>
		/// <param name="validationKey"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task<JToken> GetFilesAsync(this RequestInfo requestInfo, string objectID = null, string objectTitle = null, string validationKey = null, CancellationToken cancellationToken = default, Action<string, Exception> tracker = null, Formatting jsonFormat = Formatting.None)
			=> requestInfo == null || requestInfo.Session == null
				? Task.FromResult<JToken>(null)
				: new RequestInfo(requestInfo.Session, "Files")
				{
					Header = requestInfo.Header,
					Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
					{
						["x-object-id"] = objectID ?? requestInfo.GetObjectIdentity(),
						["x-object-title"] = objectTitle
					},
					Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
					{
						["Signature"] = (requestInfo.GetHeaderParameter("x-app-token") ?? "").GetHMACSHA256(validationKey ?? CryptoService.DEFAULT_PASS_PHRASE),
						["SessionID"] = requestInfo.Session.SessionID.GetHMACBLAKE256(validationKey ?? CryptoService.DEFAULT_PASS_PHRASE)
					},
					CorrelationID = requestInfo.CorrelationID
				}.CallServiceAsync(cancellationToken, null, null, null, tracker, jsonFormat);

		/// <summary>
		/// Gets the collection of files (thumbnails and attachment files are included) as official
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="systemID"></param>
		/// <param name="entityInfo"></param>
		/// <param name="objectID"></param>
		/// <param name="objectTitle"></param>
		/// <param name="validationKey"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task<JToken> MarkFilesAsOfficialAsync(this RequestInfo requestInfo, string systemID = null, string entityInfo = null, string objectID = null, string objectTitle = null, string validationKey = null, CancellationToken cancellationToken = default, Action<string, Exception> tracker = null, Formatting jsonFormat = Formatting.None)
			=> requestInfo == null || requestInfo.Session == null
				? Task.FromResult<JToken>(null)
				: new RequestInfo(requestInfo.Session, "Files")
				{
					Verb = "PATCH",
					Header = requestInfo.Header,
					Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
					{
						["x-service-name"] = requestInfo.ServiceName,
						["x-object-name"] = requestInfo.GetObjectName(),
						["x-system-id"] = systemID,
						["x-entity"] = entityInfo,
						["x-object-id"] = objectID ?? requestInfo.GetObjectIdentity(),
						["x-object-title"] = objectTitle
					},
					Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
					{
						["Signature"] = (requestInfo.GetHeaderParameter("x-app-token") ?? "").GetHMACSHA256(validationKey ?? CryptoService.DEFAULT_PASS_PHRASE),
						["SessionID"] = requestInfo.Session.SessionID.GetHMACBLAKE256(validationKey ?? CryptoService.DEFAULT_PASS_PHRASE)
					},
					CorrelationID = requestInfo.CorrelationID
				}.CallServiceAsync(cancellationToken, null, null, null, tracker, jsonFormat);

		/// <summary>
		/// Deletes the collection of files (thumbnails and attachment files are included)
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="systemID"></param>
		/// <param name="entityInfo"></param>
		/// <param name="objectID"></param>
		/// <param name="validationKey"></param>
		/// <param name="cancellationToken"></param>
		/// <param name="tracker"></param>
		/// <param name="jsonFormat"></param>
		/// <returns></returns>
		public static Task<JToken> DeleteFilesAsync(this RequestInfo requestInfo, string systemID = null, string entityInfo = null, string objectID = null, string validationKey = null, CancellationToken cancellationToken = default, Action<string, Exception> tracker = null, Formatting jsonFormat = Formatting.None)
			=> requestInfo == null || requestInfo.Session == null
				? Task.FromResult<JToken>(null)
				: new RequestInfo(requestInfo.Session, "Files")
				{
					Verb = "DELETE",
					Header = requestInfo.Header,
					Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
					{
						["x-service-name"] = requestInfo.ServiceName,
						["x-object-name"] = requestInfo.GetObjectName(),
						["x-system-id"] = systemID,
						["x-entity"] = entityInfo,
						["x-object-id"] = objectID ?? requestInfo.GetObjectIdentity()
					},
					Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
					{
						["Signature"] = (requestInfo.GetHeaderParameter("x-app-token") ?? "").GetHMACSHA256(validationKey ?? CryptoService.DEFAULT_PASS_PHRASE),
						["SessionID"] = requestInfo.Session.SessionID.GetHMACBLAKE256(validationKey ?? CryptoService.DEFAULT_PASS_PHRASE)
					},
					CorrelationID = requestInfo.CorrelationID
				}.CallServiceAsync(cancellationToken, null, null, null, tracker, jsonFormat);

		static string GetObjectName(this RequestInfo requestInfo)
		{
			var nameElements = requestInfo.ObjectName.ToArray(".");
			return nameElements.Length > 1 ? nameElements[1] : nameElements[0];
		}
	}
}