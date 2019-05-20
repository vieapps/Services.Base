#region Related components
using System;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Dynamic;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using WampSharp.V2.Core.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Logging;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Extension methods for working with services in the VIEApps NGX
	/// </summary>
	public static partial class Extensions
	{

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
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static async Task<string> GetLocationAsync(this Session session, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			correlationID = correlationID ?? UtilityService.NewUUID;
			try
			{
				var service = Router.GetService("IPLocations");
				var response = await service.ProcessRequestAsync(new RequestInfo(session, "IPLocations")
				{
					CorrelationID = correlationID
				}, cancellationToken).ConfigureAwait(false);

				var city = response.Get<string>("City");
				var region = response.Get<string>("Region");
				if (region.Equals(city) && !"N/A".IsEquals(city))
					region = "";
				var country = response.Get<string>("Country");

				if ("N/A".IsEquals(city) && "N/A".IsEquals(region) && "N/A".IsEquals(country))
				{
					if ("Unknown".IsEquals(Extensions.CurrentLocation))
					{
						response = await service.ProcessRequestAsync(new RequestInfo(session, "IPLocations", "Current")
						{
							CorrelationID = correlationID
						}, cancellationToken).ConfigureAwait(false);
						city = response.Get<string>("City");
						region = response.Get<string>("Region");
						if (region.Equals(city) && !"N/A".IsEquals(city))
							region = "";
						country = response.Get<string>("Country");
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
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task<string> GetLocationAsync(this RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
			=> requestInfo.Session == null
				? Task.FromResult("Unknown")
				: requestInfo.Session.GetLocationAsync(requestInfo.CorrelationID, cancellationToken);
		#endregion

		#region Encryption
		/// <summary>
		/// Gest a key for encrypting/decrypting data with this session
		/// </summary>
		/// <param name="session"></param>
		/// <param name="seeds">The seeds for hashing</param>
		/// <returns></returns>
		public static byte[] GetEncryptionKey(this Session session, byte[] seeds = null)
			=> session.SessionID.GetHMACHash(seeds ?? CryptoService.DEFAULT_PASS_PHRASE.ToBytes(), "SHA512").GenerateHashKey(256);

		/// <summary>
		/// Gest a key for encrypting/decrypting data with this session
		/// </summary>
		/// <param name="session"></param>
		/// <param name="seeds">The seeds for hashing</param>
		/// <returns></returns>
		public static byte[] GetEncryptionKey(this Session session, string seeds)
			=> session.GetEncryptionKey(seeds?.ToBytes());

		/// <summary>
		/// Gest an initialize vector for encrypting/decrypting data with this session
		/// </summary>
		/// <param name="session"></param>
		/// <param name="seeds">The seeds for hashing</param>
		/// <returns></returns>
		public static byte[] GetEncryptionIV(this Session session, byte[] seeds = null)
			=> session.SessionID.GetHMACHash(seeds ?? CryptoService.DEFAULT_PASS_PHRASE.ToBytes(), "SHA256").GenerateHashKey(128);

		/// <summary>
		/// Gest an initialize vector for encrypting/decrypting data with this session
		/// </summary>
		/// <param name="session"></param>
		/// <param name="seeds">The seeds for hashing</param>
		/// <returns></returns>
		public static byte[] GetEncryptionIV(this Session session, string seeds)
			=> session.GetEncryptionIV(seeds?.ToBytes());

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
				? id.HexToBytes().Encrypt(session.GetEncryptionKey(keySeeds?.ToBytes()), session.GetEncryptionIV(ivSeeds?.ToBytes())).ToHex()
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
				? id.HexToBytes().Decrypt(session.GetEncryptionKey(keySeeds?.ToBytes()), session.GetEncryptionIV(ivSeeds?.ToBytes())).ToHex()
				: null;
		#endregion

		#region Get platform & environment info
		/// <summary>
		/// Gets the name of the runtime OS platform
		/// </summary>
		/// <returns></returns>
		public static string GetRuntimeOS()
			=> RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS" : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux" : "Windows";

		/// <summary>
		/// Gets the information of the runtime platform
		/// </summary>
		/// <returns></returns>
		public static string GetRuntimePlatform(bool getFrameworkDescription = true)
			=> (getFrameworkDescription ? $"{RuntimeInformation.FrameworkDescription} @ " : "")
			+ $"{Extensions.GetRuntimeOS()} {RuntimeInformation.OSArchitecture.ToString().ToLower()} ({(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "Macintosh; Intel Mac OS X; " : "")}{RuntimeInformation.OSDescription.Trim()})";
		#endregion

		#region Get unique name & end-point
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
			os = os ?? Extensions.GetRuntimeOS();
			return $"{name}.{user}-{host}-" + $"{platform} @ {os}".GenerateUUID();
		}

		/// <summary>
		/// Gets the unique name of a business service
		/// </summary>
		/// <param name="name">The string that presents name of a business service</param>
		/// <param name="args">The starting arguments</param>
		/// <returns>The string that presents unique name of a business service at a host</returns>
		public static string GetUniqueName(string name, string[] args = null)
		{
			var user = args?.FirstOrDefault(a => a.IsStartsWith("/run-user:"));
			var host = args?.FirstOrDefault(a => a.IsStartsWith("/run-host:"));
			var platform = args?.FirstOrDefault(a => a.IsStartsWith("/run-platform:"));
			var os = args?.FirstOrDefault(a => a.IsStartsWith("/run-os:"));
			return Extensions.GetUniqueName(name, user?.Replace(StringComparison.OrdinalIgnoreCase, "/run-user:", "").Trim().UrlDecode(), host?.Replace(StringComparison.OrdinalIgnoreCase, "/run-host:", "").Trim().UrlDecode(), platform?.Replace(StringComparison.OrdinalIgnoreCase, "/run-platform:", "").Trim().UrlDecode(), os?.Replace(StringComparison.OrdinalIgnoreCase, "/run-os:", "").Trim().UrlDecode());
		}

		/// <summary>
		/// Gets the resolved URI with IP address and port
		/// </summary>
		/// <param name="uri"></param>
		/// <returns></returns>
		public static string GetResolvedURI(this Uri uri)
		{
			var host = "";
			if (!IPAddress.TryParse(uri.Host, out IPAddress address))
			{
				address = Dns.GetHostAddresses(uri.Host).FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork || ip.AddressFamily == AddressFamily.InterNetworkV6);
				host = address == null
					? $" => Could not resolve host \"{host}\""
					: $" => {uri.Scheme}://{new IPEndPoint(address, uri.Port)}{uri.PathAndQuery}";
			}
			return $"{uri}{host}";
		}
		#endregion

	}
}