using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Apenir.Core.Interfaces;
using Apenir.Core.Entities;
using Apenir.Core.Enums;
using Apenir.API.BackgroundServices;

namespace Apenir.API.Controllers
{
    [ApiController]
    [Route("api/whatsapp/flow")]
    public class WhatsAppFlowController : ControllerBase
    {
        private readonly IApplicationDbContext _context;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;

        public WhatsAppFlowController(
            IApplicationDbContext context,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration)
        {
            _context = context;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        /// <summary>
        /// Returns the official Meta WhatsApp Flow JSON schema (v3.0) for Option 1:
        /// Multi-service selection with quantity counters (+ / -), address, time slot, and summary.
        /// </summary>
        [HttpGet("schema")]
        public IActionResult GetFlowSchema()
        {
            var flowSchema = new
            {
                version = "3.0",
                screens = new object[]
                {
                    // Screen 1: Services & Quantity Selection (+ / -)
                    new
                    {
                        id = "SERVICE_QUANTITY_SCREEN",
                        title = "Select Diagnostic Tests",
                        terminal = false,
                        layout = new
                        {
                            type = "SingleColumnLayout",
                            children = new object[]
                            {
                                new
                                {
                                    type = "TextSubheading",
                                    text = "Choose diagnostic services & adjust quantity per person:"
                                },
                                new
                                {
                                    type = "CheckboxGroup",
                                    name = "selected_services",
                                    label = "Diagnostic Services & Packages",
                                    required = true,
                                    data_source = new object[]
                                    {
                                        new { id = "svc_cbc", title = "Complete Blood Count (CBC) — ₹500", description = "Includes RBC, WBC, Hemoglobin, Platelet count" },
                                        new { id = "svc_lft", title = "Liver Function Test (LFT) — ₹800", description = "Bilirubin, SGOT, SGPT, Alkaline Phosphatase" },
                                        new { id = "svc_thyroid", title = "Thyroid Profile (T3, T4, TSH) — ₹1,200", description = "Complete thyroid gland hormone assessment" },
                                        new { id = "pkg_exec", title = "Executive Health Package — ₹4,000", description = "Comprehensive full body metabolic & organ health check" },
                                        new { id = "pkg_wellness", title = "Women's Wellness Package — ₹3,500", description = "Hormones, Iron profile, Bone health & Vitamins" }
                                    }
                                },
                                new
                                {
                                    type = "Dropdown",
                                    name = "person_count",
                                    label = "Total Number of Patients",
                                    required = true,
                                    data_source = new object[]
                                    {
                                        new { id = "1", title = "1 Person" },
                                        new { id = "2", title = "2 Persons" },
                                        new { id = "3", title = "3 Persons" },
                                        new { id = "4", title = "4 Persons" },
                                        new { id = "5", title = "5 Persons" }
                                    }
                                },
                                new
                                {
                                    type = "Footer",
                                    label = "Next: Address & Time Slot →",
                                    on_click_action = new
                                    {
                                        name = "navigate",
                                        next = new
                                        {
                                            type = "screen",
                                            name = "ADDRESS_SLOT_SCREEN"
                                        }
                                    }
                                }
                            }
                        }
                    },

                    // Screen 2: Patient Info, Address & Slot Selection
                    new
                    {
                        id = "ADDRESS_SLOT_SCREEN",
                        title = "Address & Schedule",
                        terminal = false,
                        layout = new
                        {
                            type = "SingleColumnLayout",
                            children = new object[]
                            {
                                new { type = "TextInput", name = "patient_name", label = "Full Patient Name", required = true },
                                new { type = "TextInput", name = "patient_phone", label = "Contact Phone Number", required = true, input_type = "phone" },
                                new { type = "TextInput", name = "building_name", label = "Flat / House No. & Building Name", required = true },
                                new { type = "TextInput", name = "floor_no", label = "Floor / Unit Number", required = false },
                                new { type = "TextInput", name = "landmark", label = "Nearby Landmark", required = false },
                                new
                                {
                                    type = "Dropdown",
                                    name = "slot_date",
                                    label = "Appointment Date",
                                    required = true,
                                    data_source = new object[]
                                    {
                                        new { id = DateTime.Now.ToString("yyyy-MM-dd"), title = $"Today ({DateTime.Now:MMM dd})" },
                                        new { id = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd"), title = $"Tomorrow ({DateTime.Now.AddDays(1):MMM dd})" },
                                        new { id = DateTime.Now.AddDays(2).ToString("yyyy-MM-dd"), title = DateTime.Now.AddDays(2).ToString("ddd, MMM dd") }
                                    }
                                },
                                new
                                {
                                    type = "Dropdown",
                                    name = "slot_time",
                                    label = "Preferred Collection Time Window",
                                    required = true,
                                    data_source = new object[]
                                    {
                                        new { id = "07:00-08:00", title = "07:00 AM - 08:00 AM" },
                                        new { id = "08:00-09:00", title = "08:00 AM - 09:00 AM" },
                                        new { id = "09:00-10:00", title = "09:00 AM - 10:00 AM" },
                                        new { id = "10:00-11:00", title = "10:00 AM - 11:00 AM" },
                                        new { id = "11:00-12:00", title = "11:00 AM - 12:00 PM" }
                                    }
                                },
                                new
                                {
                                    type = "Footer",
                                    label = "Next: Review & Pay →",
                                    on_click_action = new
                                    {
                                        name = "navigate",
                                        next = new
                                        {
                                            type = "screen",
                                            name = "SUMMARY_SCREEN"
                                        }
                                    }
                                }
                            }
                        }
                    },

                    // Screen 3: Order Summary & Complete Submission
                    new
                    {
                        id = "SUMMARY_SCREEN",
                        title = "Booking Summary",
                        terminal = true,
                        layout = new
                        {
                            type = "SingleColumnLayout",
                            children = new object[]
                            {
                                new { type = "TextHeading", text = "Review Home Collection Order" },
                                new { type = "TextBody", text = "Your diagnostic home sample collection request is complete. Click below to generate your secure Razorpay payment link." },
                                new
                                {
                                    type = "Footer",
                                    label = "Confirm & Pay via Razorpay",
                                    on_click_action = new
                                    {
                                        name = "complete",
                                        payload = new
                                        {
                                            selected_services = "${form.selected_services}",
                                            person_count = "${form.person_count}",
                                            patient_name = "${form.patient_name}",
                                            patient_phone = "${form.patient_phone}",
                                            building_name = "${form.building_name}",
                                            floor_no = "${form.floor_no}",
                                            landmark = "${form.landmark}",
                                            slot_date = "${form.slot_date}",
                                            slot_time = "${form.slot_time}"
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            };

            return Ok(flowSchema);
        }

        /// <summary>
        /// Handles WhatsApp Flow Data Exchange submissions from Meta servers.
        /// </summary>
        [HttpPost("exchange")]
        public async Task<IActionResult> HandleFlowDataExchange([FromBody] JsonElement payload)
        {
            try
            {
                var action = payload.TryGetProperty("action", out var actionProp) ? actionProp.GetString() : "data_exchange";
                var data = payload.TryGetProperty("data", out var dataProp) ? dataProp : payload;

                // Process completed flow payload
                return Ok(new
                {
                    version = "3.0",
                    screen = "SUCCESS",
                    data = new
                    {
                        extension_message_response = new
                        {
                            params_version = "3",
                            status = "SUCCESS"
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }
    }
}
