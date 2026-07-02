// ============================================================
//  Apenir – Full Database Seed Script
//  Run with:
//    mongosh "mongodb+srv://workbridgeanandhu:P%40ssword1@cluster0.dseaj.mongodb.net/ApenirDb" seed.js
// ============================================================

const db = db.getSiblingDB("ApenirDb");

// ── Helpers ─────────────────────────────────────────────────
function uuid() { return UUID().toString().replace(/^UUID\("/, "").replace(/"\)$/, ""); }
const now     = new Date();
const daysAgo = d => new Date(now - d * 86400000);
const hoursAgo= h => new Date(now - h * 3600000);
const addDays = d => new Date(+now + d * 86400000);
const addMins = m => new Date(+now + m * 60000);

// ── Fixed IDs (so FK references work) ───────────────────────
const ID = {
  superadmin:  "user_superadmin_001",
  cust001:     "user_cust_001",
  cust002:     "user_cust_002",
  cust003:     "user_cust_003",
  cust004:     "user_cust_004",
  cust005:     "user_cust_005",
  cust006:     "user_cust_006",
  staff001:    "user_staff_001",
  staff002:    "user_staff_002",
  staff003:    "user_staff_003",
  lab001:      "user_lab_001",
  lab002:      "user_lab_002",
  lab003:      "user_lab_003",
  branch001:   "branch_lab_001",
  branch002:   "branch_lab_002",
  branch003:   "branch_lab_003",
  svcBlood:    "service_blood",
  svcUrine:    "service_urine",
  svcEcg:      "service_ecg",
  svcXray:     "service_xray",
  svcFull:     "service_full",
  svcThyroid:  "service_thyroid",
  svcDiabetes: "service_diabetes",
  svcCovid:    "service_covid",
  appt001:     "appt_001",
  appt002:     "appt_002",
  appt003:     "appt_003",
  slot001:     "slot_001",
  slot002:     "slot_002",
  slot003:     "slot_003",
};

print("🚀 Starting Apenir seed...\n");

// ══════════════════════════════════════════════════════════════
// 1.  ADMINS
// ══════════════════════════════════════════════════════════════
db.admins.drop();
db.admins.insertMany([
  {
    _id: UUID(), Email: "admin@apenir.com", FullName: "Super Admin",
    PasswordHash: "$2a$11$examplehashforpassword1234567890abcdef",
    IsActive: true, IsDeleted: false,
    Roles: ["SuperAdmin", "Admin"], Permissions: ["all"],
    LastLoginAt: hoursAgo(1), CreatedAt: daysAgo(90), UpdatedAt: null
  },
  {
    _id: UUID(), Email: "ops.manager@apenir.com", FullName: "Ravi Menon",
    PasswordHash: "$2a$11$examplehashforpassword1234567890abcdef",
    IsActive: true, IsDeleted: false,
    Roles: ["Admin", "OpsManager"],
    Permissions: ["branches.read", "appointments.read", "reports.read", "payrolls.manage"],
    LastLoginAt: hoursAgo(3), CreatedAt: daysAgo(30), UpdatedAt: null
  },
  {
    _id: UUID(), Email: "support@apenir.com", FullName: "Priya Nair",
    PasswordHash: "$2a$11$examplehashforpassword1234567890abcdef",
    IsActive: true, IsDeleted: false,
    Roles: ["Support"], Permissions: ["customers.read", "appointments.read"],
    LastLoginAt: hoursAgo(1), CreatedAt: daysAgo(15), UpdatedAt: null
  }
]);
print("✅ admins      → 3 records");

// ══════════════════════════════════════════════════════════════
// 2.  USERS
//     Role: Customer=1  Staff=2  Lab=3  SuperAdmin=4
// ══════════════════════════════════════════════════════════════
db.users.drop();
db.users.insertMany([
  // SuperAdmin
  { _id: ID.superadmin, Name: "Super Admin User", Email: "admin@apenir.com",         Phone: "1800111111", Role: 4, PasswordHash: "$2a$11$examplehashforpassword1234567890abcdef", IsActive: true, CreatedAt: daysAgo(90), UpdatedAt: null },
  // Customers
  { _id: ID.cust001,   Name: "Ananya Sharma",   Email: "ananya.sharma@gmail.com",   Phone: "9876543210", Role: 1, IsActive: true,  CreatedAt: daysAgo(45), UpdatedAt: null },
  { _id: ID.cust002,   Name: "Rohit Nair",       Email: "rohit.nair@yahoo.com",      Phone: "9876543211", Role: 1, IsActive: true,  CreatedAt: daysAgo(30), UpdatedAt: null },
  { _id: ID.cust003,   Name: "Meera Pillai",     Email: "meera.pillai@outlook.com",  Phone: "9876543212", Role: 1, IsActive: true,  CreatedAt: daysAgo(20), UpdatedAt: null },
  { _id: ID.cust004,   Name: "Arjun Menon",      Email: "arjun.menon@gmail.com",     Phone: "9876543213", Role: 1, IsActive: true,  CreatedAt: daysAgo(10), UpdatedAt: null },
  { _id: ID.cust005,   Name: "Divya Krishnan",   Email: "divya.k@gmail.com",         Phone: "9876543214", Role: 1, IsActive: false, CreatedAt: daysAgo(60), UpdatedAt: null },
  { _id: ID.cust006,   Name: "Sanjay Verma",     Email: "sanjay.v@gmail.com",        Phone: "9876543215", Role: 1, IsActive: true,  CreatedAt: daysAgo(5),  UpdatedAt: null },
  // Staff
  { _id: ID.staff001,  Name: "Arun Phlebotomist",Email: "arun.phlebo@apenir.com",    Phone: "9000000101", Role: 2, IsActive: true, CreatedAt: daysAgo(60), UpdatedAt: null },
  { _id: ID.staff002,  Name: "Kavitha Collector", Email: "kavitha.c@apenir.com",     Phone: "9000000102", Role: 2, IsActive: true, CreatedAt: daysAgo(50), UpdatedAt: null },
  { _id: ID.staff003,  Name: "Thomas Technician", Email: "thomas.t@apenir.com",      Phone: "9000000103", Role: 2, IsActive: true, CreatedAt: daysAgo(40), UpdatedAt: null },
  // Lab managers
  { _id: ID.lab001,    Name: "Lal Manager",       Phone: "1800123456", Role: 3, IsActive: true, CreatedAt: daysAgo(90), UpdatedAt: null },
  { _id: ID.lab002,    Name: "SRL Manager",        Phone: "1800123457", Role: 3, IsActive: true, CreatedAt: daysAgo(90), UpdatedAt: null },
  { _id: ID.lab003,    Name: "Metro Manager",      Phone: "1800123458", Role: 3, IsActive: true, CreatedAt: daysAgo(90), UpdatedAt: null },
]);
print("✅ users       → 13 records");

// ══════════════════════════════════════════════════════════════
// 3.  CUSTOMERS
//     Gender: Male=1  Female=2  Other=3
// ══════════════════════════════════════════════════════════════
db.customers.drop();
db.customers.insertMany([
  { _id: uuid(), UserId: ID.cust001, DateOfBirth: "1992-03-14", gender: 2, Address: "12A, Rose Apartments, MG Road, Kochi",              District: "kochi"      },
  { _id: uuid(), UserId: ID.cust002, DateOfBirth: "1988-07-22", gender: 1, Address: "45, Green Villa, Edapally, Kochi",                  District: "kochi"      },
  { _id: uuid(), UserId: ID.cust003, DateOfBirth: "1995-11-05", gender: 2, Address: "Flat 2C, Sunrise Tower, Palarivattom, Kochi",        District: "kochi"      },
  { _id: uuid(), UserId: ID.cust004, DateOfBirth: "1985-01-30", gender: 1, Address: "8, Jubilee Hills, Trivandrum",                       District: "trivandrum" },
  { _id: uuid(), UserId: ID.cust005, DateOfBirth: "1999-06-18", gender: 2, Address: "22, Palm Grove, Thrissur",                          District: "thrissur"   },
  { _id: uuid(), UserId: ID.cust006, DateOfBirth: "1978-09-02", gender: 1, Address: "Flat 7D, Marina Enclave, Kozhikode",                District: "kozhikode"  },
  { _id: uuid(), UserId: ID.superadmin, DateOfBirth: "1990-05-15", gender: 1, Address: "Flat 4B, Emerald Residency, Kochi",              District: "kochi"      },
]);
print("✅ customers   → 7 records");

// ══════════════════════════════════════════════════════════════
// 4.  SERVICES
// ══════════════════════════════════════════════════════════════
db.services.drop();
db.services.insertMany([
  { _id: ID.svcBlood,    Name: "Blood Test",         Description: "CBC, LFT, RFT, Lipid profile & more",    Category: "Biochemistry",  BasePrice: NumberDecimal("400.00"),  PlatformCommissionPct: NumberDecimal("15.00"), IsActive: true,  CreatedAt: daysAgo(90)  },
  { _id: ID.svcUrine,    Name: "Urine Analysis",      Description: "Routine & microscopy urine exam",        Category: "Hematology",    BasePrice: NumberDecimal("150.00"),  PlatformCommissionPct: NumberDecimal("15.00"), IsActive: true,  CreatedAt: daysAgo(90)  },
  { _id: ID.svcEcg,      Name: "ECG",                 Description: "12-lead electrocardiogram",              Category: "Cardiology",    BasePrice: NumberDecimal("300.00"),  PlatformCommissionPct: NumberDecimal("15.00"), IsActive: true,  CreatedAt: daysAgo(90)  },
  { _id: ID.svcXray,     Name: "X-Ray",               Description: "Chest X-Ray single view",               Category: "Radiology",     BasePrice: NumberDecimal("500.00"),  PlatformCommissionPct: NumberDecimal("15.00"), IsActive: true,  CreatedAt: daysAgo(90)  },
  { _id: ID.svcFull,     Name: "Full Body Checkup",   Description: "Comprehensive standard body checkup",   Category: "Biochemistry",  BasePrice: NumberDecimal("1500.00"), PlatformCommissionPct: NumberDecimal("15.00"), IsActive: true,  CreatedAt: daysAgo(90)  },
  { _id: ID.svcThyroid,  Name: "Thyroid Profile",     Description: "TSH, T3, T4 levels panel",              Category: "Endocrinology", BasePrice: NumberDecimal("350.00"),  PlatformCommissionPct: NumberDecimal("12.00"), IsActive: true,  CreatedAt: daysAgo(60)  },
  { _id: ID.svcDiabetes, Name: "Diabetes Screening",  Description: "Fasting glucose, HbA1c, post-prandial", Category: "Biochemistry",  BasePrice: NumberDecimal("250.00"),  PlatformCommissionPct: NumberDecimal("10.00"), IsActive: true,  CreatedAt: daysAgo(60)  },
  { _id: ID.svcCovid,    Name: "COVID-19 RT-PCR",     Description: "Real-time PCR test for SARS-CoV-2",     Category: "Microbiology",  BasePrice: NumberDecimal("800.00"),  PlatformCommissionPct: NumberDecimal("20.00"), IsActive: false, CreatedAt: daysAgo(180) },
]);
print("✅ services    → 8 records");

// ══════════════════════════════════════════════════════════════
// 5.  BRANCHES
// ══════════════════════════════════════════════════════════════
db.branches.drop();
db.branches.insertMany([
  { _id: ID.branch001, LabUserId: ID.lab001, Name: "Lal PathLabs — Kochi",  District: "kochi",      City: "Kochi",      Pincode: "682016", Latitude: NumberDecimal("9.9788"),  Longitude: NumberDecimal("76.2798"), Phone: "1800123456", IsActive: true, CreatedBy: ID.superadmin, CreatedAt: daysAgo(90) },
  { _id: ID.branch002, LabUserId: ID.lab002, Name: "SRL Diagnostics",        District: "kochi",      City: "Kochi",      Pincode: "682025", Latitude: NumberDecimal("9.9912"),  Longitude: NumberDecimal("76.3012"), Phone: "1800123457", IsActive: true, CreatedBy: ID.superadmin, CreatedAt: daysAgo(90) },
  { _id: ID.branch003, LabUserId: ID.lab003, Name: "Metro Diagnostics",      District: "trivandrum", City: "Trivandrum", Pincode: "695001", Latitude: NumberDecimal("8.5241"),  Longitude: NumberDecimal("76.9366"), Phone: "1800123458", IsActive: true, CreatedBy: ID.superadmin, CreatedAt: daysAgo(90) },
]);
print("✅ branches    → 3 records");

// ══════════════════════════════════════════════════════════════
// 6.  BRANCH SERVICES (pricing overrides per branch)
// ══════════════════════════════════════════════════════════════
db.branch_services.drop();
db.branch_services.insertMany([
  { _id: uuid(), BranchId: ID.branch001, ServiceId: ID.svcBlood,    CustomPrice: NumberDecimal("450.00"), CustomCommissionPct: NumberDecimal("15.00"), IsActive: true },
  { _id: uuid(), BranchId: ID.branch001, ServiceId: ID.svcFull,     CustomPrice: NumberDecimal("1400.00"),CustomCommissionPct: NumberDecimal("15.00"), IsActive: true },
  { _id: uuid(), BranchId: ID.branch001, ServiceId: ID.svcThyroid,  CustomPrice: NumberDecimal("320.00"), CustomCommissionPct: NumberDecimal("12.00"), IsActive: true },
  { _id: uuid(), BranchId: ID.branch002, ServiceId: ID.svcBlood,    CustomPrice: NumberDecimal("420.00"), CustomCommissionPct: NumberDecimal("15.00"), IsActive: true },
  { _id: uuid(), BranchId: ID.branch002, ServiceId: ID.svcEcg,      CustomPrice: NumberDecimal("280.00"), CustomCommissionPct: NumberDecimal("15.00"), IsActive: true },
  { _id: uuid(), BranchId: ID.branch002, ServiceId: ID.svcDiabetes, CustomPrice: NumberDecimal("230.00"), CustomCommissionPct: NumberDecimal("10.00"), IsActive: true },
  { _id: uuid(), BranchId: ID.branch003, ServiceId: ID.svcBlood,    CustomPrice: NumberDecimal("400.00"), CustomCommissionPct: NumberDecimal("15.00"), IsActive: true },
  { _id: uuid(), BranchId: ID.branch003, ServiceId: ID.svcXray,     CustomPrice: NumberDecimal("480.00"), CustomCommissionPct: NumberDecimal("15.00"), IsActive: true },
  { _id: uuid(), BranchId: ID.branch003, ServiceId: ID.svcUrine,    CustomPrice: NumberDecimal("140.00"), CustomCommissionPct: NumberDecimal("15.00"), IsActive: true },
]);
print("✅ branch_services → 9 records");

// ══════════════════════════════════════════════════════════════
// 7.  BRANCH SLOT CONFIGURATIONS  (weekly schedule templates)
//     DayText: Mon=1 Tue=2 Wed=3 Thu=4 Fri=5 Sat=6 Sun=7
// ══════════════════════════════════════════════════════════════
db.branch_slot_configurations.drop();
const slotTimes = [
  { start: "06:00:00", end: "07:00:00" },
  { start: "07:00:00", end: "08:00:00" },
  { start: "09:00:00", end: "10:00:00" },
  { start: "11:00:00", end: "12:00:00" },
  { start: "12:00:00", end: "13:00:00" },
  { start: "14:00:00", end: "15:00:00" },
  { start: "16:00:00", end: "17:00:00" },
];
const slotConfigs = [];
[ID.branch001, ID.branch002, ID.branch003].forEach(branchId => {
  for (let day = 1; day <= 7; day++) {
    slotTimes.forEach(t => {
      slotConfigs.push({
        _id: uuid(), BranchId: branchId, DayText: day,
        StartTime: t.start, EndTime: t.end, MaxCapacity: 3, IsLeave: false
      });
    });
  }
});
db.branch_slot_configurations.insertMany(slotConfigs);
print(`✅ branch_slot_configurations → ${slotConfigs.length} records`);

// ══════════════════════════════════════════════════════════════
// 8.  APPOINTMENT SLOTS  (calendar slots for next 7 days)
// ══════════════════════════════════════════════════════════════
db.appointment_slots.drop();
const calendarSlots = [];
const branches = [ID.branch001, ID.branch002, ID.branch003];
for (let i = 0; i < 7; i++) {
  const d    = new Date(now); d.setDate(d.getDate() + i);
  const yyyy = d.getFullYear();
  const mm   = String(d.getMonth() + 1).padStart(2, "0");
  const dd   = String(d.getDate()).padStart(2, "0");
  const dateStr = `${yyyy}-${mm}-${dd}`;

  branches.forEach(branchId => {
    slotTimes.forEach((t, idx) => {
      // Mark first slot of branch001 as partially booked for realism
      const bookedCount = (branchId === ID.branch001 && i === 0 && idx === 2) ? 1 : 0;
      calendarSlots.push({
        _id: (branchId === ID.branch001 && i === 0 && idx === 2) ? ID.slot001 : uuid(),
        BranchId: branchId, SlotDate: dateStr,
        StartTime: t.start, EndTime: t.end,
        MaxCapacity: 3, BookedCount: bookedCount, IsAvailable: true
      });
    });
  });
}
db.appointment_slots.insertMany(calendarSlots);
print(`✅ appointment_slots → ${calendarSlots.length} records`);

// ══════════════════════════════════════════════════════════════
// 9.  APPOINTMENTS
//     Status: Pending=1 Confirmed=2 Assigned=3 Collected=4 Completed=5 Cancelled=6
// ══════════════════════════════════════════════════════════════
db.appointments.drop();
db.appointments.insertMany([
  {
    _id: ID.appt001,
    AppointmentNumber: `BK-${now.getFullYear()}0620-0001`,
    CustomerUserId: ID.cust001, BranchId: ID.branch001, AppointmentSlotId: ID.slot001,
    LocationLatitude: NumberDecimal("9.9788"), LocationLongitude: NumberDecimal("76.2798"),
    LocationAddress: "12A, Rose Apartments, MG Road, Kochi",
    Passcode: "1234", Status: 5,           // Completed
    TotalAmount: NumberDecimal("450.00"), PlatformCommission: NumberDecimal("67.50"), LabPayout: NumberDecimal("382.50"),
    AssignedStaffId: ID.staff001, CreatedAt: daysAgo(5), UpdatedAt: daysAgo(4)
  },
  {
    _id: ID.appt002,
    AppointmentNumber: `BK-${now.getFullYear()}0625-0002`,
    CustomerUserId: ID.cust002, BranchId: ID.branch002, AppointmentSlotId: uuid(),
    LocationLatitude: NumberDecimal("9.9912"), LocationLongitude: NumberDecimal("76.3012"),
    LocationAddress: "45, Green Villa, Edapally, Kochi",
    Passcode: "5678", Status: 3,           // Assigned
    TotalAmount: NumberDecimal("300.00"), PlatformCommission: NumberDecimal("45.00"), LabPayout: NumberDecimal("255.00"),
    AssignedStaffId: ID.staff002, CreatedAt: daysAgo(2), UpdatedAt: daysAgo(1)
  },
  {
    _id: ID.appt003,
    AppointmentNumber: `BK-${now.getFullYear()}0701-0003`,
    CustomerUserId: ID.cust004, BranchId: ID.branch003, AppointmentSlotId: uuid(),
    LocationLatitude: NumberDecimal("8.5241"), LocationLongitude: NumberDecimal("76.9366"),
    LocationAddress: "8, Jubilee Hills, Trivandrum",
    Passcode: "9012", Status: 1,           // Pending
    TotalAmount: NumberDecimal("500.00"), PlatformCommission: NumberDecimal("75.00"), LabPayout: NumberDecimal("425.00"),
    AssignedStaffId: null, CreatedAt: hoursAgo(6), UpdatedAt: null
  },
]);
print("✅ appointments → 3 records");

// ══════════════════════════════════════════════════════════════
// 10.  APPOINTMENT MEMBERS
//      Gender: Male=1  Female=2
// ══════════════════════════════════════════════════════════════
const memberId001 = uuid();
const memberId002 = uuid();
const memberId003 = uuid();
db.appointment_members.drop();
db.appointment_members.insertMany([
  { _id: memberId001, AppointmentId: ID.appt001, MemberName: "Ananya Sharma",  Age: 32, Gender: 2, Relationship: "Self",   AdditionalNotes: "Fasting 12 hours. Diabetic — handle with care." },
  { _id: memberId002, AppointmentId: ID.appt001, MemberName: "Raman Sharma",   Age: 65, Gender: 1, Relationship: "Father", AdditionalNotes: "Hypertension. Requires gentle blood draw." },
  { _id: memberId003, AppointmentId: ID.appt002, MemberName: "Rohit Nair",     Age: 35, Gender: 1, Relationship: "Self",   AdditionalNotes: "No special instructions." },
  { _id: uuid(),      AppointmentId: ID.appt003, MemberName: "Arjun Menon",    Age: 40, Gender: 1, Relationship: "Self",   AdditionalNotes: "Fasting from midnight." },
]);
print("✅ appointment_members → 4 records");

// ══════════════════════════════════════════════════════════════
// 11.  REPORTS
// ══════════════════════════════════════════════════════════════
db.reports.drop();
db.reports.insertMany([
  {
    _id: uuid(), AppointmentId: ID.appt001, MemberId: memberId001,
    FileUrl:  "https://s3.amazonaws.com/apenir-reports/CBC_AnanyaSharma_appt001.pdf",
    FileName: "CBC_Report_AnanyaSharma.pdf",
    UploadedBy: ID.lab001, WhatsappSent: true, CreatedAt: daysAgo(4)
  },
  {
    _id: uuid(), AppointmentId: ID.appt001, MemberId: memberId002,
    FileUrl:  "https://s3.amazonaws.com/apenir-reports/CBC_RamanSharma_appt001.pdf",
    FileName: "CBC_Report_RamanSharma.pdf",
    UploadedBy: ID.lab001, WhatsappSent: true, CreatedAt: daysAgo(4)
  },
  {
    _id: uuid(), AppointmentId: ID.appt002, MemberId: memberId003,
    FileUrl:  "https://s3.amazonaws.com/apenir-reports/ECG_RohitNair_appt002.pdf",
    FileName: "ECG_Report_RohitNair.pdf",
    UploadedBy: ID.lab002, WhatsappSent: false, CreatedAt: daysAgo(1)
  },
]);
print("✅ reports     → 3 records");

// ══════════════════════════════════════════════════════════════
// 12.  PAYMENTS
//      Status: Created=1 Paid=2 Failed=3 Refunded=4
//      PaymentMethod: UPI=1 Card=2 NetBanking=3
// ══════════════════════════════════════════════════════════════
db.payments.drop();
db.payments.insertMany([
  {
    _id: uuid(), AppointmentId: ID.appt001,
    RazorpayOrderId: "order_Qxyz001Demo", RazorpayPaymentId: "pay_Qabc001Demo",
    Status: 2, PaymentMethod: 1,   // Paid / UPI
    PaidAt: daysAgo(5), CreatedAt: daysAgo(5)
  },
  {
    _id: uuid(), AppointmentId: ID.appt002,
    RazorpayOrderId: "order_Qxyz002Demo", RazorpayPaymentId: "pay_Qabc002Demo",
    Status: 2, PaymentMethod: 2,   // Paid / Card
    PaidAt: daysAgo(2), CreatedAt: daysAgo(2)
  },
  {
    _id: uuid(), AppointmentId: ID.appt003,
    RazorpayOrderId: "order_Qxyz003Demo", RazorpayPaymentId: null,
    Status: 1, PaymentMethod: null, // Created / pending
    PaidAt: null, CreatedAt: hoursAgo(6)
  },
]);
print("✅ payments    → 3 records");

// ══════════════════════════════════════════════════════════════
// 13.  PAYROLLS
//      Status: Pending=1  Settled=2
// ══════════════════════════════════════════════════════════════
db.payrolls.drop();
db.payrolls.insertMany([
  {
    _id: uuid(), BranchId: ID.branch001, PeriodType: "Weekly",
    PeriodStart: "2026-06-23", PeriodEnd: "2026-06-29",
    GrossAmount: NumberDecimal("1350.00"), PlatformCommission: NumberDecimal("202.50"), NetPayout: NumberDecimal("1147.50"),
    Status: 2, CreatedAt: daysAgo(3)   // Settled
  },
  {
    _id: uuid(), BranchId: ID.branch001, PeriodType: "Weekly",
    PeriodStart: "2026-06-30", PeriodEnd: "2026-07-06",
    GrossAmount: NumberDecimal("450.00"), PlatformCommission: NumberDecimal("67.50"), NetPayout: NumberDecimal("382.50"),
    Status: 1, CreatedAt: daysAgo(1)   // Pending
  },
  {
    _id: uuid(), BranchId: ID.branch002, PeriodType: "Weekly",
    PeriodStart: "2026-06-23", PeriodEnd: "2026-06-29",
    GrossAmount: NumberDecimal("840.00"), PlatformCommission: NumberDecimal("126.00"), NetPayout: NumberDecimal("714.00"),
    Status: 2, CreatedAt: daysAgo(3)   // Settled
  },
  {
    _id: uuid(), BranchId: ID.branch003, PeriodType: "Weekly",
    PeriodStart: "2026-06-30", PeriodEnd: "2026-07-06",
    GrossAmount: NumberDecimal("500.00"), PlatformCommission: NumberDecimal("75.00"), NetPayout: NumberDecimal("425.00"),
    Status: 1, CreatedAt: hoursAgo(6)  // Pending
  },
]);
print("✅ payrolls    → 4 records");

// ══════════════════════════════════════════════════════════════
// 14.  STAFF ORDER LOGS
//      Status: Assigned=1 Coming=2 Reached=3 Collected=4
// ══════════════════════════════════════════════════════════════
db.staff_order_logs.drop();
db.staff_order_logs.insertMany([
  { _id: uuid(), AppointmentId: ID.appt001, StaffId: ID.staff001, Status: 1, Note: "Order assigned to Arun.",                       ContextData: '{"battery":92,"network":"4G"}',                    LoggedAt: daysAgo(5)       },
  { _id: uuid(), AppointmentId: ID.appt001, StaffId: ID.staff001, Status: 2, Note: "En route to customer location.",                 ContextData: '{"battery":88,"network":"4G","eta_mins":12}',      LoggedAt: daysAgo(5)       },
  { _id: uuid(), AppointmentId: ID.appt001, StaffId: ID.staff001, Status: 3, Note: "Reached customer address.",                     ContextData: '{"battery":85,"network":"4G","location_accuracy":"5m"}', LoggedAt: daysAgo(5)  },
  { _id: uuid(), AppointmentId: ID.appt001, StaffId: ID.staff001, Status: 4, Note: "Blood sample collected successfully.",           ContextData: '{"battery":80,"samples":2,"tubes":"EDTA,SST"}',    LoggedAt: daysAgo(4)       },
  { _id: uuid(), AppointmentId: ID.appt002, StaffId: ID.staff002, Status: 1, Note: "ECG order assigned to Kavitha.",                ContextData: '{"battery":95,"network":"WiFi"}',                  LoggedAt: daysAgo(2)       },
  { _id: uuid(), AppointmentId: ID.appt002, StaffId: ID.staff002, Status: 3, Note: "Reached Edapally location.",                    ContextData: '{"battery":90,"network":"4G","location_accuracy":"8m"}', LoggedAt: daysAgo(1)  },
]);
print("✅ staff_order_logs → 6 records");

// ══════════════════════════════════════════════════════════════
// 15.  OTP CODES
// ══════════════════════════════════════════════════════════════
db.otp_codes.drop();
db.otp_codes.insertMany([
  { _id: uuid(), Phone: "9876543210", HashCode: "$2a$11$otp482910hashedvalue0000000000000", ExpiresAt: daysAgo(1),  Attempts: 1 },  // Expired
  { _id: uuid(), Phone: "9876543211", HashCode: "$2a$11$otp371842hashedvalue0000000000000", ExpiresAt: addMins(10), Attempts: 0 },  // Active
  { _id: uuid(), Phone: "9876543212", HashCode: "$2a$11$otp193847hashedvalue0000000000000", ExpiresAt: daysAgo(1),  Attempts: 3 },  // Expired & max attempts
  { _id: uuid(), Phone: "9876543213", HashCode: "$2a$11$otp562034hashedvalue0000000000000", ExpiresAt: addMins(8),  Attempts: 0 },  // Active
  { _id: uuid(), Phone: "9000000101", HashCode: "$2a$11$otp774521hashedvalue0000000000000", ExpiresAt: addMins(12), Attempts: 1 },  // Active, 1 attempt
]);
print("✅ otp_codes   → 5 records");

// ══════════════════════════════════════════════════════════════
// 16.  WHATSAPP SESSIONS
//      WhatsAppState: Start=0 ChoosingTest=1 ChoosingCity=2 ChoosingLab=3
//                     ChoosingSlot=4 MemberCount=5 Location=6 Confirm=7
//                     AwaitingPayment=8 Done=9
// ══════════════════════════════════════════════════════════════
db.whatsapp_sessions.drop();
db.whatsapp_sessions.insertMany([
  { _id: uuid(), Phone: "9876543210", CurrentState: 9 /* Done */,           SelectedTestId: ID.svcBlood,    SelectedCity: "Kochi",      SelectedLabId: ID.branch001, SelectedLabName: "Lal PathLabs — Kochi", SelectedSlot: "2026-07-02T09:00:00", MemberCount: 2, LocationShared: true,  Passcode: "8421", UpdatedAt: hoursAgo(0) },
  { _id: uuid(), Phone: "9876543211", CurrentState: 4 /* ChoosingSlot */,   SelectedTestId: ID.svcFull,     SelectedCity: "Kochi",      SelectedLabId: ID.branch002, SelectedLabName: "SRL Diagnostics",      SelectedSlot: null,                  MemberCount: 1, LocationShared: false, Passcode: null,   UpdatedAt: hoursAgo(0) },
  { _id: uuid(), Phone: "9876543212", CurrentState: 2 /* ChoosingCity */,   SelectedTestId: ID.svcThyroid,  SelectedCity: null,         SelectedLabId: null,         SelectedLabName: null,                   SelectedSlot: null,                  MemberCount: 0, LocationShared: false, Passcode: null,   UpdatedAt: hoursAgo(1) },
  { _id: uuid(), Phone: "9876543213", CurrentState: 8 /* AwaitingPayment */,SelectedTestId: ID.svcEcg,      SelectedCity: "Trivandrum", SelectedLabId: ID.branch003, SelectedLabName: "Metro Diagnostics",    SelectedSlot: "2026-07-03T11:00:00", MemberCount: 1, LocationShared: true,  Passcode: "3397", UpdatedAt: hoursAgo(0) },
  { _id: uuid(), Phone: "9000000101", CurrentState: 0 /* Start */,          SelectedTestId: null,           SelectedCity: null,         SelectedLabId: null,         SelectedLabName: null,                   SelectedSlot: null,                  MemberCount: 0, LocationShared: false, Passcode: null,   UpdatedAt: hoursAgo(0) },
]);
print("✅ whatsapp_sessions → 5 records");

// ══════════════════════════════════════════════════════════════
// 17.  REFRESH TOKENS
// ══════════════════════════════════════════════════════════════
db.refresh_tokens.drop();
db.refresh_tokens.insertMany([
  { _id: UUID(), Token: "rt_ananya_active_001",   TokenHash: "$2a$11$rtananyahashedtokenvalue000000", UserId: ID.cust001,    ExpiresAt: addDays(7),  CreatedAt: hoursAgo(2),  RevokedAt: null,    RevokedByIp: null,        ReplacedByToken: null, CreatedByIp: "103.21.58.11",  DeviceName: "iPhone 15 Pro",       UserAgent: "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0)",         IpAddress: "103.21.58.11",  IsRevoked: false },
  { _id: UUID(), Token: "rt_rohit_active_001",    TokenHash: "$2a$11$rtrohithashedtokenvalue0000000", UserId: ID.cust002,    ExpiresAt: addDays(5),  CreatedAt: hoursAgo(8),  RevokedAt: null,    RevokedByIp: null,        ReplacedByToken: null, CreatedByIp: "49.206.12.44",   DeviceName: "Samsung Galaxy S24",  UserAgent: "Mozilla/5.0 (Linux; Android 14; SM-S928B)",        IpAddress: "49.206.12.44",  IsRevoked: false },
  { _id: UUID(), Token: "rt_arjun_revoked_001",   TokenHash: "$2a$11$rtarjunhashedtokenvalue0000000", UserId: ID.cust004,    ExpiresAt: daysAgo(1),  CreatedAt: daysAgo(8),   RevokedAt: daysAgo(1), RevokedByIp: "49.206.13.55", ReplacedByToken: null, CreatedByIp: "49.206.13.55", DeviceName: "OnePlus 12",          UserAgent: "Mozilla/5.0 (Linux; Android 14; CPH2573)",         IpAddress: "49.206.13.55",  IsRevoked: true  },
  { _id: UUID(), Token: "rt_staff_arun_001",      TokenHash: "$2a$11$rtarunhashedtokenvalue00000000", UserId: ID.staff001,   ExpiresAt: addDays(6),  CreatedAt: hoursAgo(1),  RevokedAt: null,    RevokedByIp: null,        ReplacedByToken: null, CreatedByIp: "192.168.1.10",   DeviceName: "Android Tablet",      UserAgent: "ApenirApp/2.1 (Android 13)",                       IpAddress: "192.168.1.10",  IsRevoked: false },
  { _id: UUID(), Token: "rt_superadmin_001",      TokenHash: "$2a$11$rtsuperadminhashedtoken000000",  UserId: ID.superadmin, ExpiresAt: addDays(7),  CreatedAt: hoursAgo(0),  RevokedAt: null,    RevokedByIp: null,        ReplacedByToken: null, CreatedByIp: "127.0.0.1",      DeviceName: "MacBook Pro",         UserAgent: "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)",  IpAddress: "127.0.0.1",     IsRevoked: false },
]);
print("✅ refresh_tokens → 5 records");

print("\n🎉 Apenir seed complete! All 17 collections populated.");
print("   Re-run the app now — AnyAsync() guards will skip re-seeding.");
