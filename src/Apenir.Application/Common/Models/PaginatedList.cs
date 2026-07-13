using System.Collections.Generic;

namespace Apenir.Application.Common.Models
{
    public class PaginatedList<T>
    {
        public List<T> Items { get; set; } = new();
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public int RowsPerPage { get; set; }
        public int PageCount { get; set; }
        public int TotalRows { get; set; }
    }
}
