using Autodesk.Revit.UI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TNovCommon;
using TNovCommon.Server;

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
                // CreateDirectory сам проверяет существование — отдельный Exists лишь добавлял запрос к серверу.
                ServerDirectories.Ensure(RootFolder);
                ServerDirectories.Ensure(RequestFoldersDir);
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

            // TODO: перенести на счётчик API (/counters/{name}/next) — файловый счётчик временный.
            // Файл открывается эксклюзивно (FileShare.None): чтение, инкремент и запись идут через
            // один дескриптор, поэтому два пользователя не получат одинаковый номер. Если файл занят
            // другим пользователем — повторяем с паузой, но не дольше ~5 с.
            var timeout = TimeSpan.FromSeconds(5);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rnd = new Random();
            while (true)
            {
                try
                {
                    using (var fs = new FileStream(CounterFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        int nextNumber = 1;
                        var encoding = new System.Text.UTF8Encoding(false);
                        using (var reader = new StreamReader(fs, encoding, true, 1024, leaveOpen: true))
                        {
                            string content = reader.ReadToEnd().Trim();
                            if (int.TryParse(content, out int lastNumber))
                                nextNumber = lastNumber + 1;
                        }

                        byte[] bytes = encoding.GetBytes(nextNumber.ToString());
                        fs.SetLength(0);
                        fs.Position = 0;
                        fs.Write(bytes, 0, bytes.Length);
                        fs.Flush(true);
                        return nextNumber.ToString("D4");
                    }
                }
                catch (IOException) when (sw.Elapsed < timeout)
                {
                    // Файл держит другой пользователь (sharing violation) — ждём 100–200 мс.
                    System.Threading.Thread.Sleep(100 + rnd.Next(101));
                }
            }
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
                Directory.CreateDirectory(path); // no-op, если папка уже есть
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