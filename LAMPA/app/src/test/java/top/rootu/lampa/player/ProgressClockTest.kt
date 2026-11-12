package top.rootu.lampa.player
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
class ProgressClockTest {
    @Test fun distinctActionsWithTheSameElapsedTimeRemainOrdered() {
        val clock=ProgressClock(100000){1000L}
        assertEquals(100001,clock.next());assertEquals(100002,clock.next())
    }
    @Test fun elapsedTimeAdvancesTheUnixAnchor() {
        var mono=1000L;val clock=ProgressClock(100000){mono}
        clock.next();mono+=30000
        assertEquals(130000,clock.next())
    }
    @Test fun nonIncreasingClockInputCannotRegressAnAction() {
        var mono=1000L;val clock=ProgressClock(100000){mono}
        val first=clock.next();mono=0
        assertTrue(clock.next()>first)
    }
}
