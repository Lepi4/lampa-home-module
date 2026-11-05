package top.rootu.lampa.player

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class VideoFrameGateTest {
    @Test fun frameFromOldItemDoesNotClearNewCover() {
        val gate = VideoFrameGate()
        gate.onItem("live:A", "surface-1", rendered = 10)
        assertFalse(gate.hasFrame())
        assertFalse(gate.onFrame("live:B", "surface-1"))
        assertFalse(gate.hasFrame())
        assertTrue(gate.onFrame("live:A", "surface-1"))
        assertTrue(gate.hasFrame())
    }

    @Test fun frameFromOldOutputDoesNotClearNewCover() {
        val gate = VideoFrameGate()
        gate.onItem("live:A", "surface-1", rendered = 10)
        gate.onOutput("surface-2", rendered = 20)
        assertFalse(gate.onFrame("live:A", "surface-1"))
        assertFalse(gate.hasFrame())
        assertTrue(gate.onFrame("live:A", "surface-2"))
        assertTrue(gate.hasFrame())
    }

    @Test fun newItemInvalidatesPreviousFrame() {
        val gate = VideoFrameGate()
        gate.onItem("live:A", "surface-1")
        assertTrue(gate.onFrame("live:A", "surface-1"))
        gate.onItem("live:B", "surface-1")
        assertFalse(gate.hasFrame())
        assertTrue(gate.renderedBase() == 0L)
    }

    @Test fun watchdogMarkOnlyClosesCurrentGeneration() {
        val gate = VideoFrameGate()
        val first = gate.onItem("live:A", "surface-1")
        gate.markFrame()
        assertTrue(gate.hasFrame())
        gate.onItem("live:B", "surface-1")
        assertFalse(gate.hasFrame())
        assertTrue(gate.generation > first)
    }
}
