package top.rootu.lampa

import org.junit.Assert.*
import org.junit.Test

class McDocumentStateTest {
    @Test fun initialLoadGetsItsFullStartupWindow() {
        val state = McDocumentState()
        val owner = state.begin(1000)
        state.observe(owner, false, 6000)
        assertFalse(state.needsRecovery(120999))
        assertTrue(state.needsRecovery(121001))
    }

    @Test fun errorPageRevokesReadinessAndCannotRenewThePong() {
        val state = McDocumentState()
        val owner = state.begin(1000)
        state.observe(owner, true, 2000)
        state.observe(owner, false, 10000)
        assertFalse(state.ready)
        assertEquals(2000L, state.pongAt)
        assertFalse(state.needsRecovery(55000))
        assertTrue(state.needsRecovery(55001))
    }

    @Test fun repeatedErrorResponsesDoNotPostponeRecovery() {
        val state = McDocumentState()
        val owner = state.begin(1000)
        state.observe(owner, true, 2000)
        state.observe(owner, false, 10000)
        state.observe(owner, false, 54000)
        assertTrue(state.needsRecovery(55001))
    }

    @Test fun documentThatBecomesHealthyAgainIsPreserved() {
        val state = McDocumentState()
        val owner = state.begin(1000)
        state.observe(owner, true, 2000)
        state.observe(owner, false, 10000)
        state.observe(owner, true, 54000)
        assertTrue(state.ready)
        assertFalse(state.needsRecovery(55001))
        assertTrue(state.needsRecovery(99001))
    }

    @Test fun missingCallbackAfterSuccessAlsoRequiresRecovery() {
        val state = McDocumentState()
        val owner = state.begin(1000)
        state.observe(owner, true, 2000)
        assertTrue(state.needsRecovery(47001))
    }

    @Test fun previousDocumentCannotMarkOrBreakTheNextOne() {
        val state = McDocumentState()
        val old = state.begin(1000)
        state.observe(old, true, 2000)
        val next = state.begin(50000)
        state.observe(old, true, 60000)
        assertFalse(state.ready)
        assertEquals(0L, state.pongAt)
        state.observe(next, true, 70000)
        state.observe(old, false, 80000)
        assertTrue(state.ready)
        assertEquals(70000L, state.pongAt)
        assertFalse(state.acceptsRecovery(old, 500000))
    }
}
