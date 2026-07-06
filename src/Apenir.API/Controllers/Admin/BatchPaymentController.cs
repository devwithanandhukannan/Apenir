using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Apenir.Core.Entities;
using Apenir.Core.Enums;
using Apenir.Core.Interfaces;
using Apenir.Application.Common.Interfaces;
using Apenir.Application.Common.Models;
using Apenir.Application.DTOs;

namespace Apenir.API.Controllers
{
    [ApiController]
    [Route("api/admin/batch-payments")]
    [Authorize]
    public class BatchPaymentController : ControllerBase
    {
        private readonly IApplicationDbContext _context;
        private readonly ICurrentUserService _currentUserService;

        public BatchPaymentController(
            IApplicationDbContext context,
            ICurrentUserService currentUserService)
        {
            _context = context;
            _currentUserService = currentUserService;
        }

        [HttpGet("labs/{branchId}/unbatched-payments")]
        [EndpointSummary("Get unbatched payments for a lab")]
        [EndpointDescription("Returns all customer payments with status Paid that are not part of any active batch for the specified lab/branch.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<List<UnbatchedPaymentDto>>))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetUnbatchedPayments([FromRoute] string branchId, CancellationToken cancellationToken)
        {
            var branchExists = await _context.Branches.AsNoTracking()
                .AnyAsync(b => b.Id == branchId, cancellationToken);

            if (!branchExists)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            // Find all appointments for this branch
            var appointments = await _context.Appointments.AsNoTracking()
                .Where(a => a.BranchId == branchId)
                .ToListAsync(cancellationToken);

            var appointmentIds = appointments.Select(a => a.Id).ToList();
            if (appointmentIds.Count == 0)
            {
                return Ok(ApiResponse<List<UnbatchedPaymentDto>>.SuccessResult(new List<UnbatchedPaymentDto>(), "No unbatched payments found for this lab."));
            }

            // Find all paid payments for these appointments that have no BatchId
            var payments = await _context.Payments.AsNoTracking()
                .Where(p => appointmentIds.Contains(p.AppointmentId) && p.Status == PaymentStatus.Paid && p.BatchId == null)
                .ToListAsync(cancellationToken);

            if (payments.Count == 0)
            {
                return Ok(ApiResponse<List<UnbatchedPaymentDto>>.SuccessResult(new List<UnbatchedPaymentDto>(), "No unbatched payments found for this lab."));
            }

            // Fetch customer details
            var customerUserIds = appointments
                .Select(a => a.CustomerUserId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .ToList();

            var customerUsers = await _context.Users.AsNoTracking()
                .Where(u => customerUserIds.Contains(u.Id))
                .ToListAsync(cancellationToken);

            var appointmentDict = appointments.ToDictionary(a => a.Id);
            var customerDict = customerUsers.ToDictionary(u => u.Id);

            var result = payments.Select(p =>
            {
                appointmentDict.TryGetValue(p.AppointmentId, out var appt);
                var customerName = string.Empty;
                if (appt != null && !string.IsNullOrEmpty(appt.CustomerUserId))
                {
                    customerDict.TryGetValue(appt.CustomerUserId, out var cust);
                    customerName = cust?.Name ?? string.Empty;
                }

                return new UnbatchedPaymentDto
                {
                    PaymentId = p.Id,
                    AppointmentId = p.AppointmentId,
                    AppointmentNumber = appt?.AppointmentNumber ?? string.Empty,
                    CustomerName = customerName,
                    TotalAmount = appt?.TotalAmount ?? 0,
                    PlatformCommission = appt?.PlatformCommission ?? 0,
                    LabPayout = appt?.LabPayout ?? 0,
                    PaidAt = p.PaidAt,
                    PaymentMethod = p.PaymentMethod
                };
            }).ToList();

            return Ok(ApiResponse<List<UnbatchedPaymentDto>>.SuccessResult(result, "Unbatched payments retrieved successfully."));
        }

        [HttpPost]
        [EndpointSummary("Create a payment batch")]
        [EndpointDescription("Creates a new payout batch from selected payment IDs for a lab/branch.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<PaymentBatch>))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> CreateBatch([FromBody] CreateBatchRequest request, CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.BranchId) || request.PaymentIds == null || request.PaymentIds.Count == 0)
            {
                return BadRequest(ApiResponse.FailureResult("BranchId and at least one PaymentId are required."));
            }

            var branchExists = await _context.Branches.AsNoTracking()
                .AnyAsync(b => b.Id == request.BranchId, cancellationToken);

            if (!branchExists)
            {
                return NotFound(ApiResponse.FailureResult("Branch not found."));
            }

            var distinctPaymentIds = request.PaymentIds.Distinct().ToList();

            // Fetch requested payments
            var payments = await _context.Payments
                .Where(p => distinctPaymentIds.Contains(p.Id))
                .ToListAsync(cancellationToken);

            if (payments.Count != distinctPaymentIds.Count)
            {
                return BadRequest(ApiResponse.FailureResult("One or more payment IDs are invalid or non-existent."));
            }

            // Verify all payments are Paid and not already batched
            var alreadyBatchedOrUnpaid = payments.Where(p => p.Status != PaymentStatus.Paid || !string.IsNullOrEmpty(p.BatchId)).ToList();
            if (alreadyBatchedOrUnpaid.Any())
            {
                return BadRequest(ApiResponse.FailureResult("One or more selected payments are either not Paid or already part of another batch."));
            }

            // Fetch associated appointments
            var appointmentIds = payments.Select(p => p.AppointmentId).Distinct().ToList();
            var appointments = await _context.Appointments.AsNoTracking()
                .Where(a => appointmentIds.Contains(a.Id))
                .ToListAsync(cancellationToken);

            // Verify all appointments belong to the specified branch
            var mismatchedBranchAppointments = appointments.Where(a => a.BranchId != request.BranchId).ToList();
            if (mismatchedBranchAppointments.Any())
            {
                return BadRequest(ApiResponse.FailureResult("One or more payments belong to appointments under a different branch."));
            }

            var currentUserId = _currentUserService.UserId?.ToString();

            var batch = new PaymentBatch
            {
                Id = Guid.NewGuid().ToString(),
                BranchId = request.BranchId,
                PaymentIds = distinctPaymentIds,
                AppointmentIds = appointmentIds,
                PaymentCount = distinctPaymentIds.Count,
                TotalGrossAmount = appointments.Sum(a => a.TotalAmount),
                TotalPlatformCommission = appointments.Sum(a => a.PlatformCommission),
                TotalNetPayout = appointments.Sum(a => a.LabPayout),
                Status = PaymentBatchStatus.Initiated,
                CreatedBy = currentUserId,
                Notes = request.Notes,
                CreatedAt = DateTime.UtcNow
            };

            // Link payments to the new batch
            foreach (var payment in payments)
            {
                payment.BatchId = batch.Id;
                _context.Payments.Update(payment);
            }

            _context.PaymentBatches.Add(batch);
            await _context.SaveChangesAsync(cancellationToken);

            return Ok(ApiResponse<PaymentBatch>.SuccessResult(batch, "Payment batch created successfully."));
        }

        [HttpPost("list")]
        [EndpointSummary("Search and filter payment batches")]
        [EndpointDescription("Returns a paginated list of payment batches with optional filters.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<PaginatedList<PaymentBatchListDto>>))]
        public async Task<IActionResult> ListBatches([FromBody] SearchBatchesRequest? request, CancellationToken cancellationToken)
        {
            var query = _context.PaymentBatches.AsNoTracking();

            if (request != null)
            {
                if (!string.IsNullOrWhiteSpace(request.BranchId))
                {
                    query = query.Where(pb => pb.BranchId == request.BranchId);
                }

                if (request.Status.HasValue)
                {
                    query = query.Where(pb => pb.Status == request.Status.Value);
                }

                if (request.StartDate.HasValue)
                {
                    query = query.Where(pb => pb.CreatedAt >= request.StartDate.Value);
                }

                if (request.EndDate.HasValue)
                {
                    query = query.Where(pb => pb.CreatedAt <= request.EndDate.Value);
                }
            }

            var totalRows = await query.CountAsync(cancellationToken);
            var pageNumber = request?.PageNumber ?? 1;
            var rowsPerPage = request?.RowsPerPage ?? 10;
            if (pageNumber < 1) pageNumber = 1;
            if (rowsPerPage < 1) rowsPerPage = 10;

            var pageCount = (int)Math.Ceiling((double)totalRows / rowsPerPage);

            var batches = await query
                .OrderByDescending(pb => pb.CreatedAt)
                .Skip((pageNumber - 1) * rowsPerPage)
                .Take(rowsPerPage)
                .ToListAsync(cancellationToken);

            var branchIds = batches.Select(pb => pb.BranchId).Distinct().ToList();
            var branches = await _context.Branches.AsNoTracking()
                .Where(b => branchIds.Contains(b.Id))
                .ToListAsync(cancellationToken);

            var branchDict = branches.ToDictionary(b => b.Id, b => b.Name);

            var items = batches.Select(pb => new PaymentBatchListDto
            {
                Id = pb.Id,
                BranchId = pb.BranchId,
                BranchName = branchDict.TryGetValue(pb.BranchId, out var name) ? name : string.Empty,
                PaymentCount = pb.PaymentCount,
                TotalGrossAmount = pb.TotalGrossAmount,
                TotalPlatformCommission = pb.TotalPlatformCommission,
                TotalNetPayout = pb.TotalNetPayout,
                Status = pb.Status,
                CreatedAt = pb.CreatedAt,
                ConfirmedAt = pb.ConfirmedAt,
                Notes = pb.Notes
            }).ToList();

            var response = new PaginatedList<PaymentBatchListDto>
            {
                Items = items,
                PageNumber = pageNumber,
                PageSize = items.Count,
                RowsPerPage = rowsPerPage,
                PageCount = pageCount,
                TotalRows = totalRows
            };

            return Ok(ApiResponse<PaginatedList<PaymentBatchListDto>>.SuccessResult(response, "Payment batches retrieved successfully."));
        }

        [HttpGet("{batchId}")]
        [EndpointSummary("Get payment batch details")]
        [EndpointDescription("Returns full details of a payment batch including its payments and associated appointments.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse<PaymentBatchDetailResponse>))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> GetBatchDetails([FromRoute] string batchId, CancellationToken cancellationToken)
        {
            var batch = await _context.PaymentBatches.AsNoTracking()
                .FirstOrDefaultAsync(pb => pb.Id == batchId, cancellationToken);

            if (batch == null)
            {
                return NotFound(ApiResponse.FailureResult("Payment batch not found."));
            }

            var branch = await _context.Branches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == batch.BranchId, cancellationToken);

            var payments = await _context.Payments.AsNoTracking()
                .Where(p => batch.PaymentIds.Contains(p.Id))
                .ToListAsync(cancellationToken);

            var appointments = await _context.Appointments.AsNoTracking()
                .Where(a => batch.AppointmentIds.Contains(a.Id))
                .ToListAsync(cancellationToken);

            var customerUserIds = appointments
                .Select(a => a.CustomerUserId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .ToList();

            var customerUsers = await _context.Users.AsNoTracking()
                .Where(u => customerUserIds.Contains(u.Id))
                .ToListAsync(cancellationToken);

            var appointmentDict = appointments.ToDictionary(a => a.Id);
            var customerDict = customerUsers.ToDictionary(u => u.Id);

            var paymentItems = payments.Select(p =>
            {
                appointmentDict.TryGetValue(p.AppointmentId, out var appt);
                var customerName = string.Empty;
                if (appt != null && !string.IsNullOrEmpty(appt.CustomerUserId))
                {
                    customerDict.TryGetValue(appt.CustomerUserId, out var cust);
                    customerName = cust?.Name ?? string.Empty;
                }

                return new BatchPaymentItemDto
                {
                    PaymentId = p.Id,
                    AppointmentId = p.AppointmentId,
                    AppointmentNumber = appt?.AppointmentNumber ?? string.Empty,
                    CustomerName = customerName,
                    TotalAmount = appt?.TotalAmount ?? 0,
                    PlatformCommission = appt?.PlatformCommission ?? 0,
                    LabPayout = appt?.LabPayout ?? 0,
                    PaidAt = p.PaidAt,
                    PaymentMethod = p.PaymentMethod
                };
            }).ToList();

            var response = new PaymentBatchDetailResponse
            {
                Id = batch.Id,
                BranchId = batch.BranchId,
                BranchName = branch?.Name ?? string.Empty,
                PaymentCount = batch.PaymentCount,
                TotalGrossAmount = batch.TotalGrossAmount,
                TotalPlatformCommission = batch.TotalPlatformCommission,
                TotalNetPayout = batch.TotalNetPayout,
                Status = batch.Status,
                CreatedBy = batch.CreatedBy,
                ConfirmedByLabUser = batch.ConfirmedByLabUser,
                ConfirmedAt = batch.ConfirmedAt,
                CreatedAt = batch.CreatedAt,
                Notes = batch.Notes,
                Payments = paymentItems
            };

            return Ok(ApiResponse<PaymentBatchDetailResponse>.SuccessResult(response, "Payment batch details retrieved successfully."));
        }

        [HttpDelete("{batchId}")]
        [EndpointSummary("Delete/abandon a payment batch")]
        [EndpointDescription("Hard deletes a batch and releases all its payments back to unbatched status. Only works if batch status is Initiated.")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ApiResponse))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ApiResponse))]
        public async Task<IActionResult> AbandonBatch([FromRoute] string batchId, CancellationToken cancellationToken)
        {
            var batch = await _context.PaymentBatches
                .FirstOrDefaultAsync(pb => pb.Id == batchId, cancellationToken);

            if (batch == null)
            {
                return NotFound(ApiResponse.FailureResult("Payment batch not found."));
            }

            if (batch.Status != PaymentBatchStatus.Initiated)
            {
                return BadRequest(ApiResponse.FailureResult("Only batches with 'Initiated' status can be abandoned/deleted. Settled batches cannot be deleted."));
            }

            // Release all payments in this batch
            var payments = await _context.Payments
                .Where(p => p.BatchId == batchId)
                .ToListAsync(cancellationToken);

            foreach (var payment in payments)
            {
                payment.BatchId = null;
                _context.Payments.Update(payment);
            }

            _context.PaymentBatches.Remove(batch);
            await _context.SaveChangesAsync(cancellationToken);

            return Ok(ApiResponse.SuccessResult("Payment batch abandoned and hard-deleted successfully. All associated payments have been released back to unpaid status."));
        }
    }

    public class UnbatchedPaymentDto
    {
        public string PaymentId { get; set; } = string.Empty;
        public string AppointmentId { get; set; } = string.Empty;
        public string AppointmentNumber { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public decimal TotalAmount { get; set; }
        public decimal PlatformCommission { get; set; }
        public decimal LabPayout { get; set; }
        public DateTime? PaidAt { get; set; }
        public PaymentMethod? PaymentMethod { get; set; }
    }

    public class CreateBatchRequest
    {
        public string BranchId { get; set; } = string.Empty;
        public List<string> PaymentIds { get; set; } = new();
        public string? Notes { get; set; }
    }

    public record SearchBatchesRequest(
        string? BranchId,
        PaymentBatchStatus? Status,
        DateTime? StartDate,
        DateTime? EndDate,
        int PageNumber = 1,
        int RowsPerPage = 10
    );

    public class PaymentBatchListDto
    {
        public string Id { get; set; } = string.Empty;
        public string BranchId { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty;
        public int PaymentCount { get; set; }
        public decimal TotalGrossAmount { get; set; }
        public decimal TotalPlatformCommission { get; set; }
        public decimal TotalNetPayout { get; set; }
        public PaymentBatchStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ConfirmedAt { get; set; }
        public string? Notes { get; set; }
    }

    public class PaymentBatchDetailResponse
    {
        public string Id { get; set; } = string.Empty;
        public string BranchId { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty;
        public int PaymentCount { get; set; }
        public decimal TotalGrossAmount { get; set; }
        public decimal TotalPlatformCommission { get; set; }
        public decimal TotalNetPayout { get; set; }
        public PaymentBatchStatus Status { get; set; }
        public string? CreatedBy { get; set; }
        public string? ConfirmedByLabUser { get; set; }
        public DateTime? ConfirmedAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? Notes { get; set; }
        public List<BatchPaymentItemDto> Payments { get; set; } = new();
    }

    public class BatchPaymentItemDto
    {
        public string PaymentId { get; set; } = string.Empty;
        public string AppointmentId { get; set; } = string.Empty;
        public string AppointmentNumber { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public decimal TotalAmount { get; set; }
        public decimal PlatformCommission { get; set; }
        public decimal LabPayout { get; set; }
        public DateTime? PaidAt { get; set; }
        public PaymentMethod? PaymentMethod { get; set; }
    }
}
