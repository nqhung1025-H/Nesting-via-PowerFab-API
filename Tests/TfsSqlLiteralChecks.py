"""Offline grammar check for the logged unquoted-ID failure.

Uses a disposable in-memory SQLite database, not MySQL or live PowerFab.
The JSON input is extracted from actual C# regression SaveNest requests.
"""
import json
from pathlib import Path
import sqlite3
import sys


def run(evidence_path):
    evidence = json.loads(Path(evidence_path).read_text())
    passed = 0
    connection = sqlite3.connect(":memory:")
    connection.execute("CREATE TABLE ExternalInventoryLinks (ExternalInventoryLinkID INTEGER PRIMARY KEY, ExternalInventoryID TEXT)")
    for record in evidence["logged_failures"]:
        link = record["link_id"]
        value = record["external_remnant_id"]
        connection.execute("INSERT INTO ExternalInventoryLinks VALUES (?, ?)", (link, "unchanged"))
        try:
            connection.execute(f"UPDATE ExternalInventoryLinks SET ExternalInventoryID={value} WHERE ExternalInventoryLinkID={link}")
        except sqlite3.OperationalError as error:
            assert "no such column" in str(error)
            assert connection.execute("SELECT ExternalInventoryID FROM ExternalInventoryLinks WHERE ExternalInventoryLinkID=?", (link,)).fetchone()[0] == "unchanged"
            passed += 1
        else:
            raise AssertionError("The logged unsafe token unexpectedly parsed as a data literal")
    for record in evidence["actual_requests"]:
        for value in record["remnant_ids"]:
            assert value.isascii() and value.isdecimal() and str(int(value)) == value and 0 < int(value) <= 9223372036854775807
            # The same unquoted expression structure now has a numeric literal.
            connection.execute("UPDATE ExternalInventoryLinks SET ExternalInventoryID=" + value + " WHERE ExternalInventoryLinkID=10044")
            assert connection.execute("SELECT ExternalInventoryID FROM ExternalInventoryLinks WHERE ExternalInventoryLinkID=10044").fetchone()[0] == value
            passed += 1
    print(f"PASS: {passed} in-memory SQL grammar checks using actual request IDs. SQLite only; no MySQL/PowerFab execution.")


if __name__ == "__main__":
    run(sys.argv[1] if len(sys.argv) > 1 else Path(__file__).parent / "Fixtures" / "TFS-Numeric-Ids.json")
