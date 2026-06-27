using System.Collections.Generic;

namespace Apenir.Application.Common.Models
{
    public class ApiResponse<T>
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public T? Data { get; set; }
        public List<string> Errors { get; set; } = new();

        public static ApiResponse<T> SuccessResult(T data, string message = "")
        {
            return new ApiResponse<T> { Success = true, Data = data, Message = message };
        }

        public static ApiResponse<T> FailureResult(List<string> errors, string message = "")
        {
            return new ApiResponse<T> { Success = false, Errors = errors, Message = message };
        }

        public static ApiResponse<T> FailureResult(string error, string message = "")
        {
            return new ApiResponse<T> { Success = false, Errors = new List<string> { error }, Message = message };
        }
    }

    public class ApiResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<string> Errors { get; set; } = new();

        public static ApiResponse SuccessResult(string message = "")
        {
            return new ApiResponse { Success = true, Message = message };
        }

        public static ApiResponse FailureResult(List<string> errors, string message = "")
        {
            return new ApiResponse { Success = false, Errors = errors, Message = message };
        }

        public static ApiResponse FailureResult(string error, string message = "")
        {
            return new ApiResponse { Success = false, Errors = new List<string> { error }, Message = message };
        }
    }
}
