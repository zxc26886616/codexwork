"""核对产品已确认开局、但本席尚未接入原桌的专用事故证据。"""
import re

CODE = "allocated-play-seat-not-ready"

def official_play(incident, batch, table, host, opponent=None):
    proof=incident.get("officialTable")
    if not isinstance(proof,dict) or not isinstance(table,str) or not re.fullmatch(r"[1-9][0-9]{0,17}",table):
        return None
    other=proof.get("opponent")
    if not (incident.get("code")==CODE and incident.get("batch")==batch and incident.get("site")=="BGA"
            and incident.get("table")==proof.get("table")==table and proof.get("status")=="play"
            and proof.get("self")==host and isinstance(other,str) and re.fullmatch(r"[1-9][0-9]{0,17}",other)
            and other!=host and (opponent is None or opponent==other)
            and type(incident.get("actions")) is int and incident["actions"]==0
            and type(incident.get("elapsedMs")) is int and incident["elapsedMs"]>=8000
            and type(proof.get("authorityAtUnixMs")) is int and proof["authorityAtUnixMs"]>0
            and proof.get("clockStatus")=="not-read"):
        return None
    return dict(source="officialTable",status="play",table=table,self=host,opponent=other,
                authorityAtUnixMs=proof["authorityAtUnixMs"],elapsedMs=incident["elapsedMs"],
                actions=0,clockStatus="not-read",incidentAt=incident["at"])
