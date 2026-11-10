package com.localcast.tv.pairing

import java.security.SecureRandom

class PinManager(
    private val pinLifetimeSeconds: Long = 120,
    private val maxAttempts: Int = 5
) {
    private val random = SecureRandom()
    var currentPin: String = generateNewPin()
        private set
    var createdAtMs: Long = System.currentTimeMillis()
        private set
    var failedAttempts: Int = 0
        private set

    fun generateNewPin(): String {
        val number = random.nextInt(9000) + 1000
        currentPin = number.toString()
        createdAtMs = System.currentTimeMillis()
        failedAttempts = 0
        return currentPin
    }

    fun isExpired(): Boolean {
        val elapsed = (System.currentTimeMillis() - createdAtMs) / 1000
        return elapsed >= pinLifetimeSeconds
    }

    fun remainingSeconds(): Long {
        val elapsed = (System.currentTimeMillis() - createdAtMs) / 1000
        val remaining = pinLifetimeSeconds - elapsed
        return if (remaining > 0) remaining else 0
    }

    sealed class ValidationResult {
        object Success : ValidationResult()
        object Expired : ValidationResult()
        data class InvalidPin(val remainingAttempts: Int) : ValidationResult()
        object MaxAttemptsExceeded : ValidationResult()
    }

    fun validate(candidate: String): ValidationResult {
        if (failedAttempts >= maxAttempts) {
            return ValidationResult.MaxAttemptsExceeded
        }
        if (isExpired()) {
            return ValidationResult.Expired
        }
        if (candidate == currentPin) {
            return ValidationResult.Success
        }

        failedAttempts++
        val remaining = maxAttempts - failedAttempts
        return if (remaining <= 0) {
            ValidationResult.MaxAttemptsExceeded
        } else {
            ValidationResult.InvalidPin(remaining)
        }
    }
}
