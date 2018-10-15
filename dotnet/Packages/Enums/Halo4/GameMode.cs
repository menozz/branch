using System;
using Newtonsoft.Json;
using System.Runtime.Serialization;
using Newtonsoft.Json.Converters;

namespace Branch.Packages.Enums.Halo4
{
	[JsonConverter(typeof(StringEnumConverter))]
	public enum GameMode
	{
		[EnumMember(Value = "war-games")]
		WarGames,

		[EnumMember(Value = "campaign")]
		Campaign,

		[EnumMember(Value = "spartan-ops")]
		SpartanOps,

		[EnumMember(Value = "custom-games")]
		CustomGames,
	}
}
