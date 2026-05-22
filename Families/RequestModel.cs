using System;

namespace TNovUtils
{
    public enum RequestStatus
    {
        Принято,
        В_работе,
        На_согласовании,
        Выполнено,
        Закрыто        
    }

    public class RequestModel
    {
        public string Id { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Requester { get; set; }
        public string Assignee { get; set; }
        public string Description { get; set; }
        public DateTime? Deadline { get; set; }
        public string Result { get; set; }
        public string PhotoBase64 { get; set; }
        public RequestStatus Status { get; set; } = RequestStatus.Принято;
        public string ProjectPath { get; set; }
        public string ProjectDisplayName { get; set; }
    }
}