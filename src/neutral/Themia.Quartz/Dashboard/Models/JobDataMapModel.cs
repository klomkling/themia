using System.Collections.Generic;

namespace Themia.Quartz.Dashboard.Models
{
    public class JobDataMapModel
    {
        public List<JobDataMapItem> Items { get; } = new List<JobDataMapItem>();
        public JobDataMapItem Template { get; set; }
    }
}
