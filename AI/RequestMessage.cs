using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace net.vieapps.Services.AI
{
	/// <summary>
	/// Presents the provider-independent input of an AI execution
	/// </summary>
	public class RequestMessage
	{
		/// <summary>
		/// Gets or Sets the instructions for this AI execution
		/// </summary>
		public string Instructions { get; set; }

		/// <summary>
		/// Gets or Sets the normalized JSON input to adapt for the selected provider
		/// </summary>
		public JToken Input { get; set; }

		/// <summary>
		/// Gets or Sets contextual resource references, without granting access to those resources
		/// </summary>
		public List<Reference> References { get; set; }

		/// <summary>
		/// Gets or Sets the explicit business capability allow-list; null or empty grants no capabilities
		/// </summary>
		public List<string> Capabilities { get; set; }

		/// <summary>
		/// Gets or Sets the canonical JSON schema used to validate the output; null allows unstructured output when no factory supplies a schema
		/// </summary>
		/// <remarks>An explicit schema takes precedence over SchemaFactory. Provider adaptations do not replace canonical validation.</remarks>
		public JObject Schema { get; set; }

		/// <summary>
		/// Gets or Sets the lazy business-owned schema generator, excluded from JSON serialization
		/// </summary>
		/// <remarks>When Schema is absent, execution preparation invokes this factory once before provider attempts and reuses the resulting canonical schema across retries.</remarks>
		[JsonIgnore]
		public Func<JObject> SchemaFactory { get; set; }

		/// <summary>
		/// Gets or Sets execution-specific options, including provider-specific extensions
		/// </summary>
		/// <remarks>Options override provider-profile defaults and are overridden by supported RequestInfo.Extra hints on the effective request copy.</remarks>
		public JObject Options { get; set; }
	}

	/// <summary>
	/// Presents a resource or context identity, without granting access to it
	/// </summary>
	public class Reference
	{
		/// <summary>
		/// Gets or Sets the URI identifying the contextual resource
		/// </summary>
		public string URI { get; set; }

		/// <summary>
		/// Gets or Sets the optional friendly resource title
		/// </summary>
		public string Title { get; set; }

		/// <summary>
		/// Gets or Sets the optional description of the resource context
		/// </summary>
		public string Description { get; set; }

		/// <summary>
		/// Gets or Sets resource-specific contextual options; these do not grant authorization
		/// </summary>
		public JObject Options { get; set; }
	}
}
