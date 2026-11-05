package top.rootu.lampa.player

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class LiveRetryPolicyTest {
    @Test fun delaysGrowAndStayCapped() {
        val policy = LiveRetryPolicy(longArrayOf(100, 200, 300), maxAttempts = 5)
        assertEquals(100L, policy.nextDelay())
        assertEquals(200L, policy.nextDelay())
        assertEquals(300L, policy.nextDelay())
        assertEquals(300L, policy.nextDelay())
        assertEquals(300L, policy.nextDelay())
        assertNull(policy.nextDelay())
        assertTrue(policy.exhausted)
    }

    @Test fun resetStartsANewBudget() {
        val policy = LiveRetryPolicy(maxAttempts = 1)
        assertEquals(1_500L, policy.nextDelay())
        assertNull(policy.nextDelay())
        policy.reset()
        assertEquals(1_500L, policy.nextDelay())
    }
}
