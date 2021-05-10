#region Related components
using System.Threading;
using System.Threading.Tasks;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services
{
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
		public static async Task<string> GetLocationAsync(this Session session, string correlationID = null, CancellationToken cancellationToken = default)
		{
			correlationID = correlationID ?? UtilityService.NewUUID;
			try
			{
				var service = Router.GetService("IPLocations");
				var response = await service.ProcessRequestAsync(new RequestInfo(session, "IPLocations") { CorrelationID = correlationID }, cancellationToken).ConfigureAwait(false);

				var city = response.Get("City", "N/A");
				var region = response.Get("Region", "N/A");
				if (region.Equals(city) && !"N/A".IsEquals(city))
					region = "";
				var country = response.Get("Country", "N/A");

				if ("N/A".IsEquals(city) && "N/A".IsEquals(region) && "N/A".IsEquals(country))
				{
					if ("Unknown".IsEquals(Extensions.CurrentLocation))
					{
						response = await service.ProcessRequestAsync(new RequestInfo(session, "IPLocations", "Current") { CorrelationID = correlationID }, cancellationToken).ConfigureAwait(false);
						city = response.Get("City", "N/A");
						region = response.Get("Region", "N/A");
						if (region.Equals(city) && !"N/A".IsEquals(city))
							region = "";
						country = response.Get("Country", "N/A");
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
		public static Task<string> GetLocationAsync(this RequestInfo requestInfo, CancellationToken cancellationToken = default)
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

	}
}