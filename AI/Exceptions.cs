using System;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Utility;

namespace net.vieapps.Services.AI
{
	/// <summary>
	/// Presents a normalized provider failure
	/// </summary>
	public class ProviderException : AppException
	{
		/// <summary>
		/// Creates an exception describing a normalized provider failure
		/// </summary>
		public ProviderException() : base("AI provider execution failed") { }
		/// <summary>
		/// Creates an exception describing a normalized provider failure
		/// </summary>
		/// <param name="message">The failure description</param>
		public ProviderException(string message) : base(message) { }
		/// <summary>
		/// Creates an exception describing a normalized provider failure
		/// </summary>
		/// <param name="innerException">The underlying failure</param>
		public ProviderException(Exception innerException) : base("AI provider execution failed", innerException) { }
		/// <summary>
		/// Creates an exception describing a normalized provider failure
		/// </summary>
		/// <param name="message">The failure description</param>
		/// <param name="innerException">The underlying failure</param>
		public ProviderException(string message, Exception innerException) : base(message, innerException) { }

		/// <summary>
		/// Gets or Sets whether the normalized provider failure permits a logical retry within the execution limits
		/// </summary>
		public bool IsTransient { get; set; }
		/// <summary>
		/// Gets or Sets the provider-requested retry delay, taking precedence over generated jitter
		/// </summary>
		public TimeSpan? RetryAfter { get; set; }
		/// <summary>
		/// Gets or Sets the normalized or provider-specific error code
		/// </summary>
		public string Code { get; set; }
		/// <summary>
		/// Gets or Sets selected provider diagnostics without retaining the full raw response by default
		/// </summary>
		public JObject Details { get; set; }
	}

	/// <summary>
	/// Presents an explicitly requested capability that the selected provider does not support
	/// </summary>
	public class UnsupportedCapabilityException : AppException
	{
		/// <summary>
		/// Creates an exception describing an unsupported requested capability
		/// </summary>
		public UnsupportedCapabilityException() : base("AI provider does not support the requested capability") { }
		/// <summary>
		/// Creates an exception describing an unsupported requested capability
		/// </summary>
		/// <param name="message">The failure description</param>
		public UnsupportedCapabilityException(string message) : base(message) { }
		/// <summary>
		/// Creates an exception describing an unsupported requested capability
		/// </summary>
		/// <param name="innerException">The underlying failure</param>
		public UnsupportedCapabilityException(Exception innerException) : base("AI provider does not support the requested capability", innerException) { }
		/// <summary>
		/// Creates an exception describing an unsupported requested capability
		/// </summary>
		/// <param name="message">The failure description</param>
		/// <param name="innerException">The underlying failure</param>
		public UnsupportedCapabilityException(string message, Exception innerException) : base(message, innerException) { }
	}

	/// <summary>
	/// Presents output that does not satisfy the canonical JSON schema
	/// </summary>
	public class StructuredOutputException : AppException
	{
		/// <summary>
		/// Creates an exception describing output that does not satisfy the canonical schema
		/// </summary>
		public StructuredOutputException() : base("AI output does not satisfy the expected schema") { }
		/// <summary>
		/// Creates an exception describing output that does not satisfy the canonical schema
		/// </summary>
		/// <param name="message">The failure description</param>
		public StructuredOutputException(string message) : base(message) { }
		/// <summary>
		/// Creates an exception describing output that does not satisfy the canonical schema
		/// </summary>
		/// <param name="innerException">The underlying failure</param>
		public StructuredOutputException(Exception innerException) : base("AI output does not satisfy the expected schema", innerException) { }
		/// <summary>
		/// Creates an exception describing output that does not satisfy the canonical schema
		/// </summary>
		/// <param name="message">The failure description</param>
		/// <param name="innerException">The underlying failure</param>
		public StructuredOutputException(string message, Exception innerException) : base(message, innerException) { }
	}
}
