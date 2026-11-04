"""Keep UART text offsets identical between log snapshots and incremental waits."""
def serial_log(machine):
    if not machine.serial.exists():
        return ''
    # Path.read_text uses universal newline translation. The incremental reader
    # decodes raw bytes, so collapsing CRLF there moves every later offset back
    # into earlier records. Repeated status labels can then pass on stale output.
    with machine.serial.open('r', encoding='utf-8', errors='replace', newline='') as stream:
        return stream.read()


def install(machine_type):
    if getattr(machine_type, '_zenith_raw_serial_log', False):
        return
    original_log = machine_type.log
    def log(machine):
        # In-memory/custom transports supply their own snapshots and offsets;
        # only file-backed UART machines use the raw incremental file reader.
        if getattr(machine, 'serial', None) is None:
            return original_log(machine)
        return serial_log(machine)
    machine_type.log = log
    machine_type._zenith_raw_serial_log = True
