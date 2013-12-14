﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using Branch.Core.Api.Authentication;
using Branch.Core.Storage;
using Branch.Models.Authentication;
using Branch.Models.Services.Halo4;
using Branch.Models.Services.Halo4.Branch;
using Branch.Models.Services.Halo4._343;
using Branch.Models.Services.Halo4._343.Responses;
using EasyHttp.Http;
using EasyHttp.Infrastructure;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;

namespace Branch.Core.Api.Halo4
{
	public class WaypointManager
	{
		private const string RegisterWebAppLocation =
			"https://settings.svc.halowaypoint.com/RegisterClientService.svc/register/webapp/AE5D20DCFA0347B1BCE0A5253D116752";

		private const string Language = "en-US";
		private const string Game = "h4";
		private readonly AzureStorage _storage;

		public RegisterWebApp RegisteredWebApp { get; private set; }
		public Metadata Metadata { get; private set; }
		public Playlist Playlists { get; private set; }
		public Challenge Challenges { get; private set; }


		public WaypointManager(AzureStorage storage, bool updateAuthentication = false)
		{
			_storage = storage;
			RegisterWebApp();
			
			if (updateAuthentication)
				I343.UpdateAuthentication(_storage);

			GetMetadata();
			GetPlaylists();
			GetChallenges();
		}


		#region Setup Waypoint Manager

		/// <summary>
		/// 
		/// </summary>
		public void RegisterWebApp()
		{
			var response = UnauthorizedRequest(RegisterWebAppLocation);

			if (response.StatusCode == HttpStatusCode.OK && !String.IsNullOrEmpty(response.RawText))
			{
				try
				{
					RegisteredWebApp = JsonConvert.DeserializeObject<RegisterWebApp>(response.RawText);
				}
				catch (JsonReaderException jsonReaderException)
				{
					Trace.TraceError(jsonReaderException.ToString());
					throw;
				}
			}
			else
			{
				Trace.TraceError("Unable to register web application.");
				throw new HttpException();
			}
		}

		/// <summary>
		/// 
		/// </summary>
		public void GetMetadata()
		{
			var metadata = _storage.Blob.FindAndDownloadBlob<Metadata>(_storage.Blob.H4BlobContainer,
				GenerateBlobContainerPath(BlobType.Other, "metadata"));

			if (metadata == null)
				UpdateMetadata();
			else
				Metadata = metadata;
		}

		/// <summary>
		/// 
		/// </summary>
		public void GetPlaylists()
		{
			var playlists = _storage.Blob.FindAndDownloadBlob<Playlist>(_storage.Blob.H4BlobContainer,
				GenerateBlobContainerPath(BlobType.Other, "playlists"));

			if (playlists == null)
				UpdatePlaylists();
			else
				Playlists = playlists;
		}
		
		/// <summary>
		/// 
		/// </summary>
		public void GetChallenges()
		{
			var challenges = _storage.Blob.FindAndDownloadBlob<Challenge>(_storage.Blob.H4BlobContainer,
				GenerateBlobContainerPath(BlobType.Other, "challenges"));

			if (challenges == null)
				UpdateChallenges();
			else
				Challenges = challenges;
		}

		#endregion
		

		#region Player Endpoints

		/// <summary>
		/// Gets a Players Halo 4 Service Record
		/// </summary>
		/// <param name="gamertag">The players Xbox 360 Gamertag.</param>
		/// <returns>The raw JSON of their Service Record</returns>
		public ServiceRecord GetServiceRecord(string gamertag)
		{
			var escapedGamertag = EscapeGamertag(gamertag);
			var blobContainerPath = GenerateBlobContainerPath(BlobType.PlayerServiceRecord, escapedGamertag);
			var blob = _storage.Blob.GetBlob(_storage.Blob.H4BlobContainer, blobContainerPath);
			var blobValidity = CheckBlobValidity<ServiceRecord>(blob, new TimeSpan(0, 8, 0));

			// Check if blob exists & expire date
			if (blobValidity.Item1)
				return blobValidity.Item2;

			// Try and get new blob
			var url = PopulateUrl(UrlFromIds(EndpointType.ServiceList, "GetServiceRecord"),
				new Dictionary<string, string> { { "gamertag", gamertag } });
			var serviceRecordRaw = ValidateResponseAndGetRawText(UnauthorizedRequest(url));
			var serviceRecord = ParseText<ServiceRecord>(serviceRecordRaw);
			if (serviceRecord == null) return blobValidity.Item2;

			_storage.Blob.UploadBlob(_storage.Blob.H4BlobContainer,
				GenerateBlobContainerPath(BlobType.PlayerServiceRecord, escapedGamertag), serviceRecordRaw);

			var serviceRecordEntity = JsonConvert.DeserializeObject<ServiceRecordEntity>(serviceRecordRaw);
			_storage.Table.InsertOrReplaceSingleEntity(serviceRecordEntity, _storage.Table.Halo4CloudTable);

			return serviceRecord;
		}



		#endregion

		#region Other Endpoints

		/// <summary>
		/// 
		/// </summary>
		public void UpdateMetadata()
		{
			var metaData =
				ValidateResponseAndGetRawText(
					UnauthorizedRequest(PopulateUrl(UrlFromIds(EndpointType.ServiceList, "GetGameMetadata"))));

			// Save Metadata
			_storage.Blob.UploadBlob(_storage.Blob.H4BlobContainer, GenerateBlobContainerPath(BlobType.Other, "metadata"), metaData);

			// Update in Class
			Metadata = ParseText<Metadata>(metaData);
		}

		/// <summary>
		/// 
		/// </summary>
		public void UpdatePlaylists()
		{
			var playlists =
				ValidateResponseAndGetRawText(AuthorizedRequest(PopulateUrl(UrlFromIds(EndpointType.ServiceList, "GetPlaylists")),
					AuthType.Spartan));

			// Save Metadata
			_storage.Blob.UploadBlob(_storage.Blob.H4BlobContainer, GenerateBlobContainerPath(BlobType.Other, "playlists"), playlists);

			// Update in Class
			Playlists = ParseText<Playlist>(playlists);
		}

		/// <summary>
		/// 
		/// </summary>
		public void UpdateChallenges()
		{
			var challenges =
				ValidateResponseAndGetRawText(AuthorizedRequest(PopulateUrl(UrlFromIds(EndpointType.ServiceList, "GetGlobalChallenges")),
					AuthType.Spartan));

			// Save Metadata
			_storage.Blob.UploadBlob(_storage.Blob.H4BlobContainer, GenerateBlobContainerPath(BlobType.Other, "challenges"), challenges);

			// Update in Class
			Challenges = ParseText<Challenge>(challenges);
		}

		#endregion

		#region Unauthorized Request

		private HttpResponse UnauthorizedRequest(String url)
		{
// ReSharper disable once IntroduceOptionalParameters.Local
			return UnauthorizedRequest(url, HttpMethod.GET);
		}

		private HttpResponse UnauthorizedRequest(String url, HttpMethod requestType)
		{
			return UnauthorizedRequest(url, requestType, new Dictionary<String, String>());
		}

		private HttpResponse UnauthorizedRequest(String url, HttpMethod requestType, Dictionary<String, String> headers)
		{
			if (headers == null)
				headers = new Dictionary<string, string>();

			var httpClient = new HttpClient();
			httpClient.Request.Accept = "application/json";

			foreach (var header in headers)
				httpClient.Request.AddExtraHeader(header.Key, header.Value);

			switch (requestType)
			{
				case HttpMethod.GET:
					return httpClient.Get(url);

				default:
					throw new ArgumentException();
			}
		}

		#endregion

		#region Authorized Request

		private HttpResponse AuthorizedRequest(String url, AuthType authType)
		{
// ReSharper disable once IntroduceOptionalParameters.Local
			return AuthorizedRequest(url, authType, HttpMethod.GET);
		}

		private HttpResponse AuthorizedRequest(String url, AuthType authType, HttpMethod requestType)
		{
			return AuthorizedRequest(url, authType, requestType, new Dictionary<string, string>());
		}

		private HttpResponse AuthorizedRequest(String url, AuthType authType, HttpMethod requestType,
			Dictionary<String, String> headers)
		{
			if (headers == null)
				headers = new Dictionary<string, string>();

			// get auth
			var auth = _storage.Table.RetrieveSingleEntity<WaypointTokenEntity>("Authentication", WaypointTokenEntity.FormatRowKey(),
				_storage.Table.AuthenticationCloudTable);

			switch (authType)
			{
				case AuthType.Spartan:
					headers.Add("X-343-Authorization-Spartan", auth == null ? "" : auth.SpartanToken); // error catch
					break;

				default:
					throw new ArgumentException();
			}

			return UnauthorizedRequest(url, requestType, headers);
		}

		#endregion

		#region Api Helpers

		/// <summary>
		///     Returns a URL from a key and endpoint type.
		/// </summary>
		/// <param name="endpointType">The type of endpoint you need to call (ie; ServiceList)</param>
		/// <param name="key">The key url in that endpoint.</param>
		/// <returns>A string representation of the url.</returns>
		private string UrlFromIds(EndpointType endpointType, string key)
		{
			switch (endpointType)
			{
				case EndpointType.ServiceList:
					return RegisteredWebApp.ServiceList[key];

				case EndpointType.Settings:
					return RegisteredWebApp.Settings[key];

				default:
					throw new ArgumentException();
			}
		}

		/// <summary>
		///     Populates a url with the default params populated.
		/// </summary>
		/// <param name="url">The url to populate.</param>
		/// <returns>A string representation of the populated url</returns>
		private static string PopulateUrl(string url)
		{
			return PopulateUrl(url, new Dictionary<string, string>());
		}

		/// <summary>
		///     Populates a url with the default params populated, and also populates custom params.
		/// </summary>
		/// <param name="url">The url to populate.</param>
		/// <param name="customDefaults">Custom params to populate the url with, auto wrapped in the {} brackets.</param>
		/// <returns>A string representation of the populated url</returns>
		private static string PopulateUrl(string url, Dictionary<string, string> customDefaults)
		{
			url = url.Replace("{language}", Language);
			url = url.Replace("{game}", Game);

			if (customDefaults == null)
				throw new ArgumentException("Custom Defaults can't be null");

			return customDefaults.Aggregate(url,
				(current, customDefault) => current.Replace("{" + customDefault.Key + "}", customDefault.Value));
		}

		/// <summary>
		///     Checks is a HttpResponse is valid or not.
		/// </summary>
		/// <param name="response">The HttpResponse</param>
		/// <returns>Boolean representation of the validity of the response.</returns>
		private static bool ValidateResponse(HttpResponse response)
		{
			if (response == null || response.StatusCode != HttpStatusCode.OK || String.IsNullOrEmpty(response.RawText))
				return false;

			var parsedResponse = JsonConvert.DeserializeObject<WaypointResponse>(response.RawText);
			return (parsedResponse != null && (parsedResponse.StatusCode == Enums.ResponseCode.Okay || parsedResponse.StatusCode == Enums.ResponseCode.PlayerFound));
		}

		/// <summary>
		///     Checks is a HttpResponse is valid or not, and if not returns the Raw Text.
		/// </summary>
		/// <param name="response">The HttpResponse</param>
		private static string ValidateResponseAndGetRawText(HttpResponse response)
		{
			return !ValidateResponse(response) ? null : response.RawText;
		}

		/// <summary>
		///     Checks is a HttpResponse is valid or not, and parses it into a model
		/// </summary>
		/// <param name="response">The HttpResponse we are checking and parsing</param>
		/// <returns>Returns null if the response is not valid, and the parsed model if it is.</returns>
		private static TModelType ValidateAndParseResponse<TModelType>(HttpResponse response)
			where TModelType : WaypointResponse
		{
			if (!ValidateResponse(response))
				return null;

			try
			{
				return JsonConvert.DeserializeObject<TModelType>(response.RawText);
			}
			catch (JsonReaderException jsonReaderException)
			{
				Trace.TraceError(jsonReaderException.ToString());
			}

			return null;
		}

		/// <summary>
		/// </summary>
		/// <returns></returns>
		public bool CheckApiValidity()
		{
			var auth = _storage.Table.RetrieveSingleEntity<WaypointTokenEntity>("Authentication", WaypointTokenEntity.FormatRowKey(),
				_storage.Table.AuthenticationCloudTable);

			if (auth == null)
				return false;

			return (auth.ResponseCode == 1);
		}

		#endregion

		#region Enums

		public enum AuthType
		{
			Spartan
		}

		public enum EndpointType
		{
			ServiceList,
			Settings
		}

		public enum BlobType
		{
			Other,
			PlayerServiceRecord,
			PlayerGameHistory,
			PlayerGame,
			PlayerCommendation
		}

		#endregion

		#region General Helpers

		/// <summary>
		/// 
		/// </summary>
		/// <typeparam name="TBlam"></typeparam>
		/// <param name="jsonData"></param>
		/// <returns></returns>
		public TBlam ParseText<TBlam>(string jsonData)
			where TBlam : WaypointResponse
		{
			if (jsonData == null) return null;

#if DEBUG
			return JsonConvert.DeserializeObject<TBlam>(jsonData);
#else
			try
			{
				return JsonConvert.DeserializeObject<TBlam>(jsonData);
			}
			catch (JsonReaderException jsonReaderException)
			{
				return null;
			}
#endif
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="blobType"></param>
		/// <param name="fileName"></param>
		/// <returns></returns>
		public string GenerateBlobContainerPath(BlobType blobType, string fileName)
		{
			string path;

			switch (blobType)
			{
				case BlobType.Other:
					path = "other";
					break;

				case BlobType.PlayerCommendation:
					path = "player-commendation";
					break;

				case BlobType.PlayerGame:
					path = "player-game";
					break;

				case BlobType.PlayerGameHistory:
					path = "player-game-history";
					break;

				case BlobType.PlayerServiceRecord:
					path = "player-service-record";
					break;

				default:
					throw new ArgumentException("Invalid/Unknown Blob Type");
			}

			return string.Format("{0}/{1}.json", path, fileName);
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="gamertag"></param>
		/// <returns></returns>
		public string EscapeGamertag(string gamertag)
		{
			gamertag = gamertag.ToLower();
			gamertag = gamertag.Replace(" ", "-"); // Spaces to hyphens
			return gamertag;
		}

		/// <summary>
		/// 
		/// </summary>
		/// <typeparam name="TDataModel"></typeparam>
		/// <param name="blob"></param>
		/// <param name="expireLength"></param>
		/// <returns></returns>
		public Tuple<bool, TDataModel> CheckBlobValidity<TDataModel>(ICloudBlob blob, TimeSpan expireLength)
			where TDataModel : WaypointResponse
		{
			if (blob == null || !blob.Exists()) 
				return new Tuple<bool, TDataModel>(false, null);

			var blobData = _storage.Blob.DownloadBlob<TDataModel>(blob);
			if (blobData == null) return new Tuple<bool, TDataModel>(false, null);

			if (blob.Properties.LastModified == null || DateTime.UtcNow > blob.Properties.LastModified + expireLength)
				return new Tuple<bool, TDataModel>(false, null);

			return new Tuple<bool, TDataModel>(true, blobData);
		}

		#endregion
	}
}
