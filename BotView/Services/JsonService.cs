using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BotView.Services
{
	internal class JsonService
	{
		public string FilePath { get; }

		public JsonService(string filePath)
		{
			FilePath = filePath;
		}

		/// <summary>Сохраняет JObject в файл</summary>
		public void Save(JObject data)
		{
			string? directory = Path.GetDirectoryName(FilePath);
			if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
				Directory.CreateDirectory(directory);

			string json = data.ToString(Formatting.Indented);
			File.WriteAllText(FilePath, json);
		}

		/// <summary>Загружает JObject из файла</summary>
		public JObject? Load()
		{
			if (!File.Exists(FilePath))
				return null;

			string json = File.ReadAllText(FilePath);
			return JObject.Parse(json);
		}

		/// <summary>Сохраняет массив JObject в файл</summary>
		public void SaveArray(JArray data)
		{
			string? directory = Path.GetDirectoryName(FilePath);
			if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
				Directory.CreateDirectory(directory);

			string json = data.ToString(Formatting.Indented);
			File.WriteAllText(FilePath, json);
		}

		/// <summary>Загружает JArray из файла</summary>
		public JArray? LoadArray()
		{
			if (!File.Exists(FilePath))
				return null;

			string json = File.ReadAllText(FilePath);
			return JArray.Parse(json);
		}

		/// <summary>Асинхронно сохраняет массив JObject в файл</summary>
		public async Task SaveArrayAsync(JArray data)
		{
			string? directory = Path.GetDirectoryName(FilePath);
			if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
				Directory.CreateDirectory(directory);

			string json = data.ToString(Formatting.Indented);
			await File.WriteAllTextAsync(FilePath, json);
		}

		/// <summary>Асинхронно загружает JArray из файла</summary>
		public async Task<JArray?> LoadArrayAsync()
		{
			if (!File.Exists(FilePath))
				return null;

			string json = await File.ReadAllTextAsync(FilePath);
			return JArray.Parse(json);
		}
	}
}
