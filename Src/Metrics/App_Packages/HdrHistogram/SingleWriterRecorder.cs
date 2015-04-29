﻿// Written by Gil Tene of Azul Systems, and released to the public domain,
// as explained at http://creativecommons.org/publicdomain/zero/1.0/
// 
// Ported to .NET by Iulian Margarintescu under the same license and terms as the java version
// Java Version repo: https://github.com/HdrHistogram/HdrHistogram
// Latest ported version is available in the Java submodule in the root of the repo
using System;
using System.Runtime.CompilerServices;
using Metrics.ConcurrencyUtilities;

namespace HdrHistogram
{
    /**
     * Records integer values, and provides stable interval {@link Histogram} samples from
     * live recorded data without interrupting or stalling active recording of values. Each interval
     * histogram provided contains all value counts accumulated since the previous interval histogram
     * was taken.
     * <p>
     * This pattern is commonly used in logging interval histogram information while recording is ongoing.
     * <p>
     * {@link SingleWriterRecorder} expects only a single thread (the "single writer") to
     * call {@link SingleWriterRecorder#RecordValue} or
     * {@link SingleWriterRecorder#RecordValueWithExpectedInterval} at any point in time.
     * It DOES NOT safely support concurrent recording calls.
     */

    public class SingleWriterRecorder
    {
        private static AtomicLong instanceIdSequencer = new AtomicLong(1);
        private long instanceId = instanceIdSequencer.GetAndIncrement();

        private WriterReaderPhaser recordingPhaser = new WriterReaderPhaser();

        private volatile InternalHistogram activeHistogram;
        private InternalHistogram inactiveHistogram;

        /**
         * Construct an auto-resizing {@link SingleWriterRecorder} with a lowest discernible value of
         * 1 and an auto-adjusting highestTrackableValue. Can auto-resize up to track values up to (Long.MAX_VALUE / 2).
         *
         * @param numberOfSignificantValueDigits Specifies the precision to use. This is the number of significant
         *                                       decimal digits to which the histogram will maintain value resolution
         *                                       and separation. Must be a non-negative integer between 0 and 5.
         */
        public SingleWriterRecorder(int numberOfSignificantValueDigits)
        {
            activeHistogram = new InternalHistogram(instanceId, numberOfSignificantValueDigits);
            inactiveHistogram = new InternalHistogram(instanceId, numberOfSignificantValueDigits);
            activeHistogram.setStartTimeStamp(Recorder.CurentTimeInMilis());
        }

        /**
         * Construct a {@link SingleWriterRecorder} given the highest value to be tracked and a number
         * of significant decimal digits. The histogram will be constructed to implicitly track (distinguish from 0)
         * values as low as 1.
         *
         * @param highestTrackableValue The highest value to be tracked by the histogram. Must be a positive
         *                              integer that is {@literal >=} 2.
         * @param numberOfSignificantValueDigits Specifies the precision to use. This is the number of significant
         *                                       decimal digits to which the histogram will maintain value resolution
         *                                       and separation. Must be a non-negative integer between 0 and 5.
         */
        public SingleWriterRecorder(long highestTrackableValue, int numberOfSignificantValueDigits)
            : this(1, highestTrackableValue, numberOfSignificantValueDigits)
        { }

        /**
         * Construct a {@link SingleWriterRecorder} given the Lowest and highest values to be tracked
         * and a number of significant decimal digits. Providing a lowestDiscernibleValue is useful is situations where
         * the units used for the histogram's values are much smaller that the minimal accuracy required. E.g. when
         * tracking time values stated in nanosecond units, where the minimal accuracy required is a microsecond, the
         * proper value for lowestDiscernibleValue would be 1000.
         *
         * @param lowestDiscernibleValue The lowest value that can be tracked (distinguished from 0) by the histogram.
         *                               Must be a positive integer that is {@literal >=} 1. May be internally rounded
         *                               down to nearest power of 2.
         * @param highestTrackableValue The highest value to be tracked by the histogram. Must be a positive
         *                              integer that is {@literal >=} (2 * lowestDiscernibleValue).
         * @param numberOfSignificantValueDigits Specifies the precision to use. This is the number of significant
         *                                       decimal digits to which the histogram will maintain value resolution
         *                                       and separation. Must be a non-negative integer between 0 and 5.
         */
        public SingleWriterRecorder(long lowestDiscernibleValue, long highestTrackableValue, int numberOfSignificantValueDigits)
        {
            activeHistogram = new InternalHistogram(
                    instanceId, lowestDiscernibleValue, highestTrackableValue, numberOfSignificantValueDigits);
            inactiveHistogram = new InternalHistogram(
                    instanceId, lowestDiscernibleValue, highestTrackableValue, numberOfSignificantValueDigits);
            activeHistogram.setStartTimeStamp(Recorder.CurentTimeInMilis());
        }

        /**
         * Record a value
         * @param value the value to record
         * @throws ArrayIndexOutOfBoundsException (may throw) if value is exceeds highestTrackableValue
         */
        public void recordValue(long value)
        {
            long criticalValueAtEnter = recordingPhaser.WriterCriticalSectionEnter();
            try
            {
                activeHistogram.RecordValue(value);
            }
            finally
            {
                recordingPhaser.WriterCriticalSectionExit(criticalValueAtEnter);
            }
        }

        /**
         * Record a value
         * <p>
         * To compensate for the loss of sampled values when a recorded value is larger than the expected
         * interval between value samples, Histogram will auto-generate an additional series of decreasingly-smaller
         * (down to the expectedIntervalBetweenValueSamples) value records.
         * <p>
         * See related notes {@link AbstractHistogram#RecordValueWithExpectedInterval(long, long)}
         * for more explanations about coordinated omission and expected interval correction.
         *      *
         * @param value The value to record
         * @param expectedIntervalBetweenValueSamples If expectedIntervalBetweenValueSamples is larger than 0, add
         *                                           auto-generated value records as appropriate if value is larger
         *                                           than expectedIntervalBetweenValueSamples
         * @throws ArrayIndexOutOfBoundsException (may throw) if value is exceeds highestTrackableValue
         */

        public void recordValueWithExpectedInterval(long value, long expectedIntervalBetweenValueSamples)
        {
            long criticalValueAtEnter = recordingPhaser.WriterCriticalSectionEnter();
            try
            {
                activeHistogram.RecordValueWithExpectedInterval(value, expectedIntervalBetweenValueSamples);
            }
            finally
            {
                recordingPhaser.WriterCriticalSectionExit(criticalValueAtEnter);
            }
        }

        /**
         * Get a new instance of an interval histogram, which will include a stable, consistent view of all value
         * counts accumulated since the last interval histogram was taken.
         * <p>
         * Calling {@link SingleWriterRecorder#GetIntervalHistogram()} will reset
         * the value counts, and start accumulating value counts for the next interval.
         *
         * @return a histogram containing the value counts accumulated since the last interval histogram was taken.
         */
        [MethodImpl(MethodImplOptions.Synchronized)]
        public Histogram getIntervalHistogram()
        {
            return getIntervalHistogram(null);
        }

        /**
         * Get an interval histogram, which will include a stable, consistent view of all value counts
         * accumulated since the last interval histogram was taken.
         * <p>
         * {@link SingleWriterRecorder#GetIntervalHistogram(Histogram histogramToRecycle)
         * GetIntervalHistogram(histogramToRecycle)}
         * accepts a previously returned interval histogram that can be recycled internally to avoid allocation
         * and content copying operations, and is therefore significantly more efficient for repeated use than
         * {@link SingleWriterRecorder#GetIntervalHistogram()} and
         * {@link SingleWriterRecorder#getIntervalHistogramInto getIntervalHistogramInto()}. The provided
         * {@code histogramToRecycle} must
         * be either be null or an interval histogram returned by a previous call to
         * {@link SingleWriterRecorder#GetIntervalHistogram(Histogram histogramToRecycle)
         * GetIntervalHistogram(histogramToRecycle)} or
         * {@link SingleWriterRecorder#GetIntervalHistogram()}.
         * <p>
         * NOTE: The caller is responsible for not recycling the same returned interval histogram more than once. If
         * the same interval histogram instance is recycled more than once, behavior is undefined.
         * <p>
         * Calling {@link SingleWriterRecorder#GetIntervalHistogram(Histogram histogramToRecycle)
         * GetIntervalHistogram(histogramToRecycle)} will reset the value counts, and start accumulating value
         * counts for the next interval
         *
         * @param histogramToRecycle a previously returned interval histogram that may be recycled to avoid allocation and
         *                           copy operations.
         * @return a histogram containing the value counts accumulated since the last interval histogram was taken.
         */
        [MethodImpl(MethodImplOptions.Synchronized)]
        public Histogram getIntervalHistogram(Histogram histogramToRecycle)
        {
            if (histogramToRecycle == null)
            {
                histogramToRecycle = new InternalHistogram(inactiveHistogram);
            }
            // Verify that replacement histogram can validly be used as an inactive histogram replacement:
            validateFitAsReplacementHistogram(histogramToRecycle);
            try
            {
                recordingPhaser.ReaderLock();
                inactiveHistogram = (InternalHistogram)histogramToRecycle;
                performIntervalSample();
                return inactiveHistogram;
            }
            finally
            {
                recordingPhaser.ReaderUnlock();
            }
        }

        /**
         * Place a copy of the value counts accumulated since accumulated (since the last interval histogram
         * was taken) into {@code targetHistogram}.
         *
         * Calling {@link SingleWriterRecorder#getIntervalHistogramInto getIntervalHistogramInto()} will reset
         * the value counts, and start accumulating value counts for the next interval.
         *
         * @param targetHistogram the histogram into which the interval histogram's data should be copied
         */
        [MethodImpl(MethodImplOptions.Synchronized)]
        public void getIntervalHistogramInto(Histogram targetHistogram)
        {
            performIntervalSample();
            inactiveHistogram.copyInto(targetHistogram);
        }

        /**
         * Reset any value counts accumulated thus far.
         */
        [MethodImpl(MethodImplOptions.Synchronized)]
        public void reset()
        {
            // the currently inactive histogram is reset each time we flip. So flipping twice resets both:
            performIntervalSample();
            performIntervalSample();
        }

        private void performIntervalSample()
        {
            inactiveHistogram.reset();
            try
            {
                recordingPhaser.ReaderLock();

                // Swap active and inactive histograms:
                InternalHistogram tempHistogram = inactiveHistogram;
                inactiveHistogram = activeHistogram;
                activeHistogram = tempHistogram;

                // Mark end time of previous interval and start time of new one:
                long now = Recorder.CurentTimeInMilis();
                activeHistogram.setStartTimeStamp(now);
                inactiveHistogram.setEndTimeStamp(now);

                // Make sure we are not in the middle of recording a value on the previously active histogram:

                // Flip phase to make sure no recordings that were in flight pre-flip are still active:
                recordingPhaser.FlipPhase(500000L /* yield in 0.5 msec units if needed */);
            }
            finally
            {
                recordingPhaser.ReaderUnlock();
            }
        }

        private class InternalHistogram : Histogram
        {
            public long containingInstanceId;

            public InternalHistogram(long id, int numberOfSignificantValueDigits)
                : base(numberOfSignificantValueDigits)
            {
                this.containingInstanceId = id;
            }

            public InternalHistogram(long id,
                                      long lowestDiscernibleValue,
                                      long highestTrackableValue,
                                      int numberOfSignificantValueDigits)
                : base(lowestDiscernibleValue, highestTrackableValue, numberOfSignificantValueDigits)
            {
                this.containingInstanceId = id;
            }

            public InternalHistogram(InternalHistogram source)
                : base(source)
            {
                this.containingInstanceId = source.containingInstanceId;
            }
        }

        void validateFitAsReplacementHistogram(Histogram replacementHistogram)
        {
            var internalHistogram = replacementHistogram as InternalHistogram;
            if (internalHistogram == null || internalHistogram.containingInstanceId != activeHistogram.containingInstanceId)
            {
                throw new ArgumentException("replacement histogram must have been obtained via a previous" +
                       "GetIntervalHistogram() call from this " + this.GetType().Name + " instance");
            }
        }
    }

}