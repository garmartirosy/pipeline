import psycopg2
import random
import string

DB_HOST     = "modelearth-postgres-server.postgres.database.azure.com"
DB_NAME     = "industrydb"
DB_USER     = "postgresadmin"
DB_PASSWORD = "ModelEarth11!!"
DB_PORT     = 5432
########

def random_name(length: int = 8) -> str:
    first = random.choice(string.ascii_uppercase)
    rest  = "".join(random.choices(string.ascii_lowercase, k=length - 1))
    return first + rest


def main() -> None:
    name   = random_name()
    number = random.randint(0, 1000)

    with psycopg2.connect(
        host=DB_HOST,
        dbname=DB_NAME,
        user=DB_USER,
        password=DB_PASSWORD,
        port=DB_PORT,
        sslmode="require",
    ) as conn:
        with conn.cursor() as cur:
            cur.execute(
                "INSERT INTO test_insert (name, rand_number) VALUES (%s, %s) RETURNING id",
                (name, number),
            )
            row = cur.fetchone()
            if row is None:
                raise RuntimeError("INSERT returned no row — table may not exist.")
            row_id = row[0]

    print(f"Inserted: id={row_id}, name={name}, rand_number={number}")


if __name__ == "__main__":
    main()
