"""Create a safe sample database for testing SQLiteStudio."""

from pathlib import Path
import sqlite3


TARGET = Path(__file__).with_name("sample_company.sqlite")


SCHEMA = """
PRAGMA foreign_keys = ON;

CREATE TABLE departments (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL UNIQUE
);

CREATE TABLE employees (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    email TEXT UNIQUE,
    department_id INTEGER,
    salary REAL CHECK (salary >= 0),
    active INTEGER NOT NULL DEFAULT 1,
    joined_date TEXT,
    notes TEXT,
    FOREIGN KEY (department_id) REFERENCES departments(id)
);

CREATE INDEX idx_employees_department ON employees(department_id);

CREATE VIEW active_employees AS
SELECT e.id, e.name, e.email, d.name AS department, e.salary, e.joined_date
FROM employees AS e
LEFT JOIN departments AS d ON d.id = e.department_id
WHERE e.active = 1;

CREATE TRIGGER employee_email_cleanup
AFTER UPDATE OF email ON employees
WHEN NEW.email <> trim(NEW.email)
BEGIN
    UPDATE employees SET email = trim(NEW.email) WHERE id = NEW.id;
END;

INSERT INTO departments(name) VALUES ('Engineering'), ('Finance'), ('Human Resources'), ('Operations');

INSERT INTO employees(name,email,department_id,salary,active,joined_date,notes) VALUES
('Alice Tan','alice@example.com',1,72000,1,'2024-01-15','Backend systems'),
('Bob Lim','bob@example.com',2,65000,1,'2023-08-20','Quarterly reporting'),
('Carol Wong','carol@example.com',1,81000,0,'2022-05-10',NULL),
('David Lee','david@example.com',3,59000,1,'2025-02-01','Recruitment'),
('Eva Chen','eva@example.com',4,68000,1,'2021-11-12','Regional operations');
"""


def main():
    if TARGET.exists():
        answer = input(f"{TARGET.name} already exists. Replace it? [y/N] ").strip().lower()
        if answer != "y":
            print("No changes made.")
            return
        TARGET.unlink()
    connection = sqlite3.connect(TARGET)
    try:
        connection.executescript(SCHEMA)
        connection.commit()
    finally:
        connection.close()
    print(f"Created: {TARGET}")
    print("Open this file in SQLiteStudio to test tables, views, indexes and triggers.")


if __name__ == "__main__":
    main()
