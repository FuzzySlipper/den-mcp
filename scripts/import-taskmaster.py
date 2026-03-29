#!/usr/bin/env python3
"""Import tasks from a Task Master tasks.json into Den MCP.

Usage:
    ./scripts/import-taskmaster.py <tasks.json> <den-project-id> [--den-url http://localhost:5199]
    ./scripts/import-taskmaster.py .taskmaster/tasks/tasks.json den-mcp
    ./scripts/import-taskmaster.py /home/patch/dev/other-project/.taskmaster/tasks/tasks.json other-project

The script:
  1. Reads all tasks (and subtasks) from the taskmaster JSON
  2. Creates them in Den with descriptions combining description + details + testStrategy
  3. Remaps IDs and wires up dependencies
  4. Sets final status on each task
"""

import argparse
import json
import sys
import urllib.request
import urllib.error

STATUS_MAP = {
    "done": "done",
    "pending": "planned",
    "in-progress": "in_progress",
    "blocked": "blocked",
    "deferred": "planned",
    "cancelled": "cancelled",
    "review": "review",
}

PRIORITY_MAP = {
    "high": 1,
    "medium": 3,
    "low": 5,
}


def api(base_url, method, path, body=None):
    url = f"{base_url}{path}"
    data = json.dumps(body).encode() if body else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req) as resp:
            return json.loads(resp.read())
    except urllib.error.HTTPError as e:
        err_body = e.read().decode()
        print(f"  ERROR {e.code} {method} {path}: {err_body}", file=sys.stderr)
        raise


def build_description(task):
    """Combine taskmaster fields into a single markdown description."""
    parts = []
    if task.get("description"):
        parts.append(task["description"])
    if task.get("details"):
        parts.append(f"\n## Details\n\n{task['details']}")
    if task.get("testStrategy"):
        parts.append(f"\n## Test Strategy\n\n{task['testStrategy']}")
    return "\n".join(parts) if parts else None


def import_tasks(tasks_json_path, project_id, base_url, force_done=False):
    with open(tasks_json_path) as f:
        raw = json.load(f)

    # taskmaster wraps tasks under a tag key (usually "master")
    if "master" in raw:
        tasks = raw["master"]["tasks"]
    elif "tasks" in raw:
        tasks = raw["tasks"]
    else:
        # try first key
        first_key = next(iter(raw))
        tasks = raw[first_key]["tasks"]
        print(f"  Using tag key: {first_key}")

    print(f"Found {len(tasks)} top-level tasks to import into '{project_id}'")

    # Phase 1: Create all top-level tasks (no dependencies yet)
    # old_id (string) -> new_id (int)
    id_map = {}

    for task in tasks:
        old_id = str(task["id"])
        priority = PRIORITY_MAP.get(task.get("priority", "medium"), 3)
        body = {
            "title": task["title"],
            "description": build_description(task),
            "priority": priority,
        }
        created = api(base_url, "POST", f"/api/projects/{project_id}/tasks/", body)
        new_id = created["id"]
        id_map[old_id] = new_id
        print(f"  Task {old_id} -> {new_id}: {task['title']}")

        # Create subtasks
        subtask_id_map = {}  # local subtask old_id -> new_id
        for sub in task.get("subtasks", []):
            sub_old_id = str(sub["id"])
            sub_body = {
                "title": sub["title"],
                "description": build_description(sub),
                "priority": priority,
                "parent_id": new_id,
            }
            sub_created = api(base_url, "POST", f"/api/projects/{project_id}/tasks/", sub_body)
            sub_new_id = sub_created["id"]
            subtask_id_map[sub_old_id] = sub_new_id
            print(f"    Subtask {old_id}.{sub_old_id} -> {sub_new_id}: {sub['title']}")

        # Wire subtask dependencies (within same parent)
        for sub in task.get("subtasks", []):
            sub_old_id = str(sub["id"])
            sub_new_id = subtask_id_map[sub_old_id]
            deps = [str(d) for d in sub.get("dependencies", []) if str(d) in subtask_id_map]
            if deps:
                for dep_old in deps:
                    dep_new = subtask_id_map[dep_old]
                    try:
                        api(base_url, "POST", f"/api/projects/{project_id}/tasks/{sub_new_id}/dependencies", {"depends_on": dep_new})
                        print(f"    Subtask dep: {sub_new_id} depends on {dep_new}")
                    except Exception:
                        pass  # already logged

            # Set subtask status
            sub_status = "done" if force_done else STATUS_MAP.get(sub.get("status", "pending"), "planned")
            if sub_status != "planned":
                api(base_url, "PUT", f"/api/projects/{project_id}/tasks/{sub_new_id}", {
                    "agent": "taskmaster-import",
                    "status": sub_status,
                })

    # Phase 2: Wire top-level dependencies
    for task in tasks:
        old_id = str(task["id"])
        new_id = id_map[old_id]
        deps = [str(d) for d in task.get("dependencies", []) if str(d) in id_map]
        for dep_old in deps:
            dep_new = id_map[dep_old]
            try:
                api(base_url, "POST", f"/api/projects/{project_id}/tasks/{new_id}/dependencies", {"depends_on": dep_new})
                print(f"  Dep: {new_id} depends on {dep_new}")
            except Exception:
                pass

    # Phase 3: Set final status on top-level tasks
    for task in tasks:
        old_id = str(task["id"])
        new_id = id_map[old_id]
        status = "done" if force_done else STATUS_MAP.get(task.get("status", "pending"), "planned")
        if status != "planned":
            api(base_url, "PUT", f"/api/projects/{project_id}/tasks/{new_id}", {
                "agent": "taskmaster-import",
                "status": status,
            })

    print(f"\nDone! Imported {len(tasks)} tasks into '{project_id}'")
    print(f"ID mapping: {json.dumps(id_map, indent=2)}")


def main():
    parser = argparse.ArgumentParser(description="Import Task Master tasks into Den MCP")
    parser.add_argument("tasks_json", help="Path to taskmaster tasks.json")
    parser.add_argument("project_id", help="Den project ID to import into")
    parser.add_argument("--den-url", default="http://192.168.1.10:5199", help="Den MCP server URL")
    parser.add_argument("--force-done", action="store_true", help="Set all imported tasks to done regardless of source status")
    args = parser.parse_args()

    import_tasks(args.tasks_json, args.project_id, args.den_url, force_done=args.force_done)


if __name__ == "__main__":
    main()
