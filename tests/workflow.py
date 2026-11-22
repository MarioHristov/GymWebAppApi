"""Exercise the public API using a disposable database and Python's standard library."""
import json
import sys
import uuid
from concurrent.futures import ThreadPoolExecutor
from datetime import date, timedelta
from http.cookiejar import CookieJar
from urllib.error import HTTPError
from urllib.request import HTTPCookieProcessor, ProxyHandler, Request, build_opener

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5266").rstrip("/")


class Client:
    def __init__(self):
        self.opener = build_opener(ProxyHandler({}), HTTPCookieProcessor(CookieJar()))

    def request(self, method, path, status=200, **body):
        request = Request(BASE + "/api" + path, method=method,
                          data=json.dumps(body).encode() if body else None,
                          headers={"Content-Type": "application/json", "X-Gym-Request": "1"})
        try:
            response = self.opener.open(request, timeout=20)
        except HTTPError as error:
            response = error
        with response:
            raw = response.read()
            allowed = status if isinstance(status, tuple) else (status,)
            assert response.status in allowed, (method, path, response.status, raw[:400])
            return response.status, json.loads(raw) if raw else None

    def get(self, path):
        return self.request("GET", path)[1]

    def order(self, plan, months=1, request_id=None):
        return self.request("POST", "/enrollments", planId=plan, months=months,
                            requestId=request_id or str(uuid.uuid4()))[1]


def test_workflow():
    a, b = Client(), Client()
    first_email = uuid.uuid4().hex + "@example.test"
    password = "Workflow-test-1234"
    a.request("GET", "/profile", 401)
    a.request("POST", "/account/register", email=first_email, password=password)
    b.request("POST", "/account/register", email=uuid.uuid4().hex + "@example.test", password=password)
    today = date.fromisoformat(a.get("/profile")["today"])
    a.request("PUT", "/profile", 204, name="Test Member",
              birthDate=str(today - timedelta(days=365 * 16)), student=True)
    plans = a.get("/plans")
    assert plans["discount"] == 20
    premium = next(p for p in plans["plans"] if p["id"] == "premium")
    assert [q["totalCents"] for q in premium["prices"]] == [9600, 25200, 86400]

    month = str((today.replace(day=1) + timedelta(days=32)).replace(day=1))[:7]
    schedule = "/schedule?month=" + month
    sessions = a.get(schedule)["sessions"]
    group = next(s for s in sessions if not s["personal"])
    private = next(s for s in sessions if s["personal"] and s["remaining"] == 1)
    group_path, coaching_path = ("/bookings/" + s["id"] for s in (group, private))

    def places(slot):
        return next(s["remaining"] for s in a.get(schedule)["sessions"] if s["id"] == slot["id"])

    a.request("PUT", group_path, 403)
    request_id = str(uuid.uuid4())
    order = a.order("premium", 3, request_id)
    assert order["totalCents"] == 25200 and a.order("premium", 3, request_id)["id"] == order["id"]
    renewal = a.order("premium")
    assert renewal["start"] == order["end"]
    a.request("PUT", group_path, 204)
    a.request("PUT", group_path, 204)
    assert places(group) == group["remaining"] - 1
    a.request("DELETE", group_path, 204)
    a.request("DELETE", group_path, 204)
    assert places(group) == group["remaining"]
    a.request("PUT", coaching_path, 403)
    for client in (a, b):
        client.order("pt1")

    # Independent members racing for a one-seat coaching slot must share a single winner.
    with ThreadPoolExecutor(max_workers=2) as workers:
        statuses = list(workers.map(lambda client: client.request("PUT", coaching_path, (204, 409))[0], (a, b)))
    assert sorted(statuses) == [204, 409] and places(private) == 0
    winner, loser = (a, b) if statuses[0] == 204 else (b, a)
    assert winner.get("/profile")["credits"] == 0
    loser.request("DELETE", coaching_path, 204)
    assert places(private) == 0
    winner.request("DELETE", coaching_path, 204)
    assert winner.get("/profile")["credits"] == 1 and places(private) == 1
    assert len(a.get("/profile")["history"]) == 3
    a.request("PUT", "/account/email", 403, currentPassword="incorrect", value="unused@example.test")
    new_email = "new-" + first_email
    a.request("PUT", "/account/email", 204, currentPassword=password, value=new_email)
    a.request("PUT", "/account/password", 204, currentPassword=password, value="Changed-12345")
    a.request("POST", "/account/logout", 204)
    a.request("POST", "/account/login", 401, email=first_email, password=password)
    a.request("POST", "/account/login", email=new_email, password="Changed-12345")
    assert a.get("/profile")["email"] == new_email
    assert len(a.get("/clubs")["clubs"]) == 6 and a.get("/events")
    print("PASS: membership, pricing, concurrent bookings, cancellation, credits, history and account changes.")


if __name__ == "__main__":
    test_workflow()
