# Apenir Platform — API Endpoint Documentation

This document describes all API endpoints exposed by the Apenir platform, including parameters, request schemas, response models, and security/session management.

All responses are wrapped in a standard `ApiResponse<T>` or `ApiResponse` model to ensure consistent client-side parsing.

---

## Standard Response Wrappers

All endpoints return a JSON payload formatted as either `ApiResponse` (for simple status responses) or `ApiResponse<T>` (for payload responses).

### 1. `ApiResponse`
```json
{
  "success": true, // Boolean flag indicating operation success
  "message": "string", // Summary message or status indicator
  "errors": ["string"] // List of failure messages if success is false
}
```

### 2. `ApiResponse<T>`
```json
{
  "success": true,
  "message": "string",
  "data": { ... }, // Generic payload of type T (null if success is false)
  "errors": ["string"]
}
```

---

## 1. Admin Authentication API (`/api/AdminAuth`)

Admin endpoints manage administrator sessions, tokens, and password management. Access tokens are passed via headers, while refresh tokens are securely set as `HttpOnly`, `Secure` cookies path-restricted to the `/api/AdminAuth/refresh` endpoint to prevent cross-site scripting (XSS) leaks.

### POST `/api/AdminAuth/login`
*   **Description**: Authenticates an administrator using credentials.
*   **Headers**: None
*   **Request Body**:
    ```json
    {
      "usernameOrEmail": "admin@apenir.com",
      "password": "Password123"
    }
    ```
*   **Response (200 OK)**:
    *   **Cookie set**: `admin_refresh_token` (HttpOnly, Secure, SameSite=Strict, Path=/api/AdminAuth/refresh)
    *   **Body** (`ApiResponse<LoginResponse>`):
        ```json
        {
          "success": true,
          "message": "",
          "data": {
            "accessToken": "eyJhbGciOi...",
            "refreshToken": "", // Kept empty as token is sent via secure cookie
            "expiresIn": 900, // Token lifetime in seconds (15 mins)
            "adminId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
            "username": "admin",
            "email": "admin@apenir.com"
          },
          "errors": []
        }
        ```

### POST `/api/AdminAuth/refresh`
*   **Description**: Rotates keys and returns a fresh JWT access token.
*   **Headers**: None (reads from the `admin_refresh_token` cookie)
*   **Request Body**: None (Fallback: accepts `{"refreshToken": "string"}` if cookies are not used)
*   **Response (200 OK)**:
    *   **Cookie set**: Sets new rotated `admin_refresh_token` cookie.
    *   **Body** (`ApiResponse<RefreshTokenResponse>`):
        ```json
        {
          "success": true,
          "message": "",
          "data": {
            "accessToken": "eyJhbGciOi...",
            "refreshToken": "" // Rotated refresh token placed in cookie
          },
          "errors": []
        }
        ```

### POST `/api/AdminAuth/logout`
*   **Description**: Invalidates the current refresh token and logs out the admin.
*   **Headers**: None (reads cookie)
*   **Request Body**: None (Fallback: accepts `{"refreshToken": "string"}`)
*   **Response (200 OK)** (`ApiResponse`):
    *   **Cookie cleared**: Clears `admin_refresh_token` cookie.
    *   **Body**:
        ```json
        {
          "success": true,
          "message": "Logged out successfully",
          "errors": []
        }
        ```

### POST `/api/AdminAuth/logout-all`
*   **Description**: Revokes all active refresh tokens for the authenticated administrator.
*   **Headers**: `Authorization: Bearer <AccessToken>`
*   **Request Body**: None
*   **Response (200 OK)** (`ApiResponse`):
    ```json
    {
      "success": true,
      "message": "Logged out from all devices",
      "errors": []
    }
    ```

### POST `/api/AdminAuth/change-password`
*   **Description**: Updates password for the authenticated administrator.
*   **Headers**: `Authorization: Bearer <AccessToken>`
*   **Request Body**:
    ```json
    {
      "currentPassword": "OldPassword123",
      "newPassword": "NewPassword123!" // Must contain upper, lower, digit, spec char, and >= 8 chars
    }
    ```
*   **Response (200 OK)** (`ApiResponse`):
    ```json
    {
      "success": true,
      "message": "Password changed successfully",
      "errors": []
    }
    ```

### POST `/api/AdminAuth/forgot-password`
*   **Description**: Submits a password reset request.
*   **Headers**: None
*   **Request Body**:
    ```json
    {
      "email": "admin@apenir.com"
    }
    ```
*   **Response (200 OK)** (`ApiResponse`):
    ```json
    {
      "success": true,
      "message": "Reset instructions sent if account exists",
      "errors": []
    }
    ```

### POST `/api/AdminAuth/reset-password`
*   **Description**: Completes the password reset process.
*   **Headers**: None
*   **Request Body**:
    ```json
    {
      "email": "admin@apenir.com",
      "token": "reset-token-payload",
      "newPassword": "NewPassword123!"
    }
    ```
*   **Response (200 OK)** (`ApiResponse`):
    ```json
    {
      "success": true,
      "message": "Password reset successfully",
      "errors": []
    }
    ```

### GET `/api/AdminAuth/me`
*   **Description**: Fetches profiles and metadata of the currently logged-in administrator.
*   **Headers**: `Authorization: Bearer <AccessToken>`
*   **Request Query**: None
*   **Response (200 OK)** (`ApiResponse<CurrentAdminResponse>`):
    ```json
    {
      "success": true,
      "message": "",
      "data": {
        "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
        "email": "admin@apenir.com",
        "username": "admin",
        "fullName": "Super Admin",
        "roles": ["Admin"],
        "permissions": ["ManageUsers", "ViewReports"],
        "lastLoginAt": "2026-06-28T05:59:12Z",
        "createdAt": "2026-06-01T08:00:00Z"
      },
      "errors": []
    }
    ```

### GET `/api/AdminAuth/validate-token`
*   **Description**: Validates token state.
*   **Headers**: None
*   **Request Query**: `?token=eyJhbGciOi...`
*   **Response (200 OK)** (`ApiResponse<TokenValidationResponse>`):
    ```json
    {
      "success": true,
      "message": "",
      "data": {
        "isValid": true,
        "adminId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
        "username": "admin",
        "roles": ["Admin"],
        "permissions": ["ManageUsers"]
      },
      "errors": []
    }
    ```

---

## 2. Customer Authentication API (`/api/Auth`)

This layer handles user registration and login via standard WhatsApp message validation flows.

### POST `/api/Auth/otp/send`
*   **Description**: Sends a 6-digit numeric OTP to the customer's phone number via WhatsApp.
*   **Headers**: None
*   **Request Body**:
    ```json
    {
      "phone": "+919876543210" // Required E.164 phone format
    }
    ```
*   **Response (200 OK)** (`ApiResponse`):
    ```json
    {
      "success": true,
      "message": "OTP_SENT",
      "errors": []
    }
    ```

### POST `/api/Auth/otp/verify`
*   **Description**: Validates the WhatsApp OTP, auto-registers customer profile if new, and initializes session.
*   **Headers**: None
*   **Request Body**:
    ```json
    {
      "phone": "+919876543210",
      "otp": "123456" // 6-digit verification code
    }
    ```
*   **Response (200 OK)** (`ApiResponse<AuthResponse>`):
    *   **Cookie set**: `refresh_token` (HttpOnly, Secure, SameSite=Strict, Path=/api/auth/refresh)
    *   **Body**:
        ```json
        {
          "success": true,
          "message": "",
          "data": {
            "accessToken": "eyJhbGciOi...",
            "role": "Customer",
            "phone": "+919876543210"
          },
          "errors": []
        }
        ```

### POST `/api/Auth/refresh`
*   **Description**: Refreshes JWT access token for customer.
*   **Headers**: None (reads `refresh_token` cookie)
*   **Request Body**: None
*   **Response (200 OK)** (`ApiResponse<AuthResponse>`):
    *   **Cookie set**: Rotated `refresh_token` cookie.
    *   **Body**:
        ```json
        {
          "success": true,
          "message": "",
          "data": {
            "accessToken": "eyJhbGciOi...",
            "role": "Customer",
            "phone": "+919876543210"
          },
          "errors": []
        }
        ```

---

## 3. Customer Profile API (`/api/Customer`)

Provides endpoints to read and modify details of authenticated customer profiles.

### GET `/api/Customer/profile`
*   **Description**: Retrieves profile attributes of the authenticated customer.
*   **Headers**: `Authorization: Bearer <AccessToken>` (Customer only)
*   **Request**: None
*   **Response (200 OK)** (`ApiResponse<CustomerProfileResponse>`):
    ```json
    {
      "success": true,
      "message": "",
      "data": {
        "id": "customer-guid-string",
        "name": "John Doe",
        "phone": "+919876543210",
        "gender": "Male",
        "dob": "1995-08-25",
        "district": "Ernakulam",
        "address": "123 Main St, Kochi"
      },
      "errors": []
    }
    ```

### PUT `/api/Customer/profile`
*   **Description**: Updates profile attributes for the authenticated customer.
*   **Headers**: `Authorization: Bearer <AccessToken>` (Customer only)
*   **Request Body**:
    ```json
    {
      "name": "John Doe",
      "gender": "Male",
      "dob": "1995-08-25",
      "address": "123 Main St, Kochi",
      "district": "Ernakulam"
    }
    ```
*   **Response (200 OK)** (`ApiResponse`):
    ```json
    {
      "success": true,
      "message": "PROFILE_UPDATED",
      "errors": []
    }
    ```

---

## 4. WhatsApp Webhook API (`/api/whatsapp/webhook`)

Integrates with Meta Cloud API to receive events. Webhooks are authenticated via signature verification and processed asynchronously in the background.

### GET `/api/whatsapp/webhook`
*   **Description**: Standard webhook verification endpoint required by Meta Cloud API configuration.
*   **Request Query Params**:
    *   `hub.mode` (should be `subscribe`)
    *   `hub.verify_token` (verification challenge token)
    *   `hub.challenge` (challenge response body)
*   **Response (200 OK)**: Plain-text string matching the input `hub.challenge` query value.

### POST `/api/whatsapp/webhook`
*   **Description**: Receives live event payloads (messages, location shares, quick replies) from Meta.
*   **Headers**: `X-Hub-Signature-256: sha256=<hmac-hash>` (validated via HMAC-SHA256 signature algorithm using your `WhatsApp:AppSecret`).
*   **Request Body**: JSON payload detailing events.
*   **Response (200 OK)**: Returns immediately with status `200` to acknowledge event delivery. Processing is delegated to an in-memory background worker queue.
