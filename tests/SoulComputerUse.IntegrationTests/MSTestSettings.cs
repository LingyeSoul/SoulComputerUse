using Microsoft.VisualStudio.TestTools.UnitTesting;

// The integration suite touches real machine-wide state: a real kernel lock object and real HKLM
// policy values. Method-level parallelism lets those cases race each other, which shows up as
// "the lock is held on an idle machine" through no fault of the product. The suite runs in a few
// seconds, so the ordering certainty is worth more than the saved milliseconds.
// See docs/testing-conventions.md.
[assembly: DoNotParallelize]