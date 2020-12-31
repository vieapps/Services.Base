#region Related components
using System;
using System.IO;
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

		/// <summary>
		/// Fetchs the content of a temporary file and response as Base64 string of binary data
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static async Task<JToken> FetchTemporaryFileAsync(this RequestInfo requestInfo, CancellationToken cancellationToken = default)
		{
			try
			{
				var directoryPath = UtilityService.GetAppSetting("Path:Temp", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data-files", "temp"));
				var fileName = requestInfo.GetParameter("Filename") ?? requestInfo.GetParameter("x-filename");
				if (string.IsNullOrWhiteSpace(fileName))
				{
					var request = requestInfo.GetRequestExpando();
					fileName = request.Get<string>("Filename") ?? request.Get<string>("x-filename");
				}

				var filePath = !string.IsNullOrWhiteSpace(fileName)
					? Path.Combine(directoryPath, fileName)
					: throw new FileNotFoundException();

				if (!File.Exists(filePath))
					throw new FileNotFoundException();

				var offset = (requestInfo.GetParameter("Offset") ?? requestInfo.GetParameter("x-offset") ?? "0").CastAs<long>();
				var buffer = new byte[1024 * 16];
				var read = 0;
				using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, buffer.Length, true))
				{
					if (offset < stream.Length)
					{
						if (offset > 0)
							stream.Seek(offset, SeekOrigin.Begin);
						read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
					}
				}
				return new JObject
				{
					{ "Data", read > 0 ? buffer.Take(0, read).ToBase64() : "" },
					{ "Offset", offset + read }
				};
			}
			catch (Exception)
			{
				throw;
			}
		}

		/// <summary>
		/// Downloads a temporary file
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static async Task<string> DownloadTemporaryFileAsync(this RequestInfo requestInfo, CancellationToken cancellationToken = default)
		{
			var directoryPath = UtilityService.GetAppSetting("Path:Temp", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data-files", "temp"));
			var fileName = requestInfo.GetParameter("Filename") ?? requestInfo.GetParameter("x-filename");

			if (string.IsNullOrWhiteSpace(fileName))
			{
				var request = requestInfo.GetRequestExpando();
				fileName = request.Get<string>("Filename") ?? request.Get<string>("x-filename");
			}

			var filePath = !string.IsNullOrWhiteSpace(fileName)
				? Path.Combine(directoryPath, fileName)
				: throw new FileNotFoundException();

			if (File.Exists(filePath))
				return fileName;

			requestInfo.Header["x-filename"] = fileName;
			long offset = 0;
			var service = Router.GetUniqueService(requestInfo.GetParameter("NodeID") ?? requestInfo.GetParameter("x-node"));
			while (true)
			{
				requestInfo.Header["x-offset"] = $"{offset}";
				var response = await service.FetchTemporaryFileAsync(requestInfo, cancellationToken).ConfigureAwait(false);

				var data = response.Get<string>("Data");
				if (string.IsNullOrWhiteSpace(data))
					break;

				await UtilityService.WriteBinaryFileAsync(filePath, data.Base64ToBytes(), offset > 0, cancellationToken).ConfigureAwait(false);
				offset = response.Get<long>("Offset");
			}

			return fileName;
		}
	}
}