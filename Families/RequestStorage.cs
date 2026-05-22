using Autodesk.Revit.UI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TNovCommon;

namespace TNovUtils
{
    public static class RequestStorage
    {
        /*
        private static readonly string RootFolder = AppConfig.FamilyRequestsPath;

        private static readonly string RequestsFile = Path.Combine(RootFolder, "requests.json");
        private static readonly string CounterFile = Path.Combine(RootFolder, "request_counter.txt");
        private static readonly string RequestFoldersDir = Path.Combine(RootFolder, "RequestFolders");
        */
        static RequestStorage()
        {
            TNovConfig config = TNovConfigLoad.LoadConfig();
            string RootFolder = config.ServerPath + @"familyrequests";
            string RequestFoldersDir = Path.Combine(RootFolder, "RequestFolders");
            try
            {
                if (!Directory.Exists(RootFolder))
                    Directory.CreateDirectory(RootFolder);
                if (!Directory.Exists(RequestFoldersDir))
                    Directory.CreateDirectory(RequestFoldersDir);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка создания папок хранилища: {ex.Message}");
            }
        }

        private static string GetNextId()
        {
            TNovConfig config = TNovConfigLoad.LoadConfig();
            string RootFolder = config.ServerPath + @"familyrequests";
            string CounterFile = Path.Combine(RootFolder, "request_counter.txt");
            int nextNumber = 1;
            if (File.Exists(CounterFile))
            {
                string content = File.ReadAllText(CounterFile).Trim();
                if (int.TryParse(content, out int lastNumber))
                    nextNumber = lastNumber + 1;
            }
            File.WriteAllText(CounterFile, nextNumber.ToString());
            return nextNumber.ToString("D4");
        }

        public static List<RequestModel> LoadRequests()
        {
            TNovConfig config = TNovConfigLoad.LoadConfig();
            string RootFolder = config.ServerPath + @"familyrequests";
            string RequestsFile = Path.Combine(RootFolder, "requests.json");
            if (!File.Exists(RequestsFile))
                return new List<RequestModel>();

            try
            {
                string json = File.ReadAllText(RequestsFile);
                var requests = JsonConvert.DeserializeObject<List<RequestModel>>(json);
                return requests ?? new List<RequestModel>();
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is NotSupportedException)
            {
                string backupFile = RequestsFile + ".bak";
                try
                {
                    if (File.Exists(backupFile))
                        File.Delete(backupFile);
                    File.Copy(RequestsFile, backupFile);
                    File.Delete(RequestsFile);
                }
                catch { }

                try
                {
                    new InfoWindow400("Файл заявок повреждён и был заменён новой пустой базой.\n" +
                        "Резервная копия сохранена рядом в файле: " + backupFile).ShowDialog();
                }
                catch { }

                return new List<RequestModel>();
            }
        }

        private static void SaveRequests(List<RequestModel> requests)
        {
            TNovConfig config = TNovConfigLoad.LoadConfig();
            string RootFolder = config.ServerPath + @"familyrequests";
            string RequestsFile = Path.Combine(RootFolder, "requests.json");
            try
            {
                string json = JsonConvert.SerializeObject(requests);//, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(RequestsFile, json);
            }
            catch { }
        }

        private static void CreateRequestFolder(string requestId)
        {
            TNovConfig config = TNovConfigLoad.LoadConfig();
            string RootFolder = config.ServerPath + @"familyrequests";
            string RequestFoldersDir = Path.Combine(RootFolder, "RequestFolders");
            try
            {
                string path = Path.Combine(RequestFoldersDir, requestId);
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Не удалось создать папку заявки {requestId}: {ex.Message}");
            }
        }

        private static void DeleteRequestFolder(string requestId)
        {
            TNovConfig config = TNovConfigLoad.LoadConfig();
            string RootFolder = config.ServerPath + @"familyrequests";
            string RequestFoldersDir = Path.Combine(RootFolder, "RequestFolders");
            try
            {
                string path = Path.Combine(RequestFoldersDir, requestId);
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Не удалось удалить папку заявки {requestId}: {ex.Message}");
            }
        }

        public static void AddRequest(RequestModel request)
        {
            var requests = LoadRequests();
            request.Id = GetNextId();
            requests.Add(request);
            SaveRequests(requests);
            CreateRequestFolder(request.Id);
        }

        public static void UpdateRequest(RequestModel updatedRequest)
        {
            var requests = LoadRequests();
            var index = requests.FindIndex(r => r.Id == updatedRequest.Id);
            if (index != -1)
            {
                bool becameClosed = updatedRequest.Status == RequestStatus.Закрыто
                                    && requests[index].Status != RequestStatus.Закрыто;

                requests[index] = updatedRequest;
                SaveRequests(requests);

                if (becameClosed)
                    DeleteRequestFolder(updatedRequest.Id);
            }
        }
    }
}