using Newtonsoft.Json.Linq;

namespace net.vieapps.Services.AI
{
	/// <summary>
	/// Presents normalized output and usage of an AI execution
	/// </summary>
	public class ResponseMessage
	{
		/// <summary>
		/// Gets or Sets the normalized provider output
		/// </summary>
		/// <remarks>The execution layer validates this value when a canonical schema is present. Provider refusals may have no output.</remarks>
		public JToken Output { get; set; }

		/// <summary>
		/// Gets or Sets the normalized execution outcome, preserving refusal, incomplete and required-action states
		/// </summary>
		public string Status { get; set; }

		/// <summary>
		/// Gets or Sets usage through the existing service observability contract
		/// </summary>
		/// <remarks>A provider supplies per-attempt usage. The canonical RequestInfo.ExecuteAsync entry point returns aggregated usage enriched with business request context.</remarks>
		public UsageInfo Usage { get; set; }

		/// <summary>
		/// Gets or Sets provider-specific metadata and diagnostic extensions
		/// </summary>
		/// <remarks>The reference adapters retain selected metadata rather than the full raw provider response by default.</remarks>
		public JObject Details { get; set; }
	}
}
