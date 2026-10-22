; Event log messages for the qcachelab System log source. Setup registers the installed
; driver file as this source's EventMessageFile; %1 is filled in by the I/O manager.
MessageIdTypedef=NTSTATUS

SeverityNames=(Success=0x0:STATUS_SEVERITY_SUCCESS
               Informational=0x1:STATUS_SEVERITY_INFORMATIONAL
               Warning=0x2:STATUS_SEVERITY_WARNING
               Error=0x3:STATUS_SEVERITY_ERROR)

FacilityNames=(QueueCache=0xFC:FACILITY_QUEUECACHE)

LanguageNames=(English=0x409:MSG00409)

MessageId=0x0001
Facility=QueueCache
Severity=Error
SymbolicName=QC_LOG_REMOVED_WITH_PENDING_WRITES
Language=English
QueueCache: the cached volume %2 was removed while %3 bytes of Fast-mode writes were not yet on its disk. Those writes were lost.
.

MessageId=0x0002
Facility=QueueCache
Severity=Informational
SymbolicName=QC_LOG_REMOVED
Language=English
QueueCache: the cached volume %2 was removed. No writes were waiting for its disk.
.
