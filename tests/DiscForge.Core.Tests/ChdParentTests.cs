// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
// See LICENSE at the root of this repository.

using DiscForge.Core.Chd;
using Xunit;

namespace DiscForge.Core.Tests;

/// <summary>
/// PARENT-hunk resolution in CD extraction: a delta (child) CHD whose hunks reference
/// a parent CHD. Without the parent it must be declined; with the parent it extracts
/// byte-exact (the CHD SHA-1 proves it). Fixtures are real chdman images — a 40-sector
/// MODE1 parent and a child that changes two sectors, so most of the child's hunks are
/// PARENT references.
/// </summary>
public class ChdParentTests
{
    private const string ParentB64 =
        "TUNvbXBySEQAAAB8AAAABWNkbHpjZHpsY2RmbAAAAAAAAAAAAAF+gAAAAAAAACGVAAAAAAAAAHwAAEyAAAAJkEiD3q6VPhPaBu841eA5z9bJXZngq55i7m" +
        "aLbq8snwkotyn7bnte0UIAAAAAAAAAAAAAAAAAAAAAAAAAAENIVDIBAABZAAAAAAAAAABUUkFDSzoxIFRZUEU6TU9ERTFfUkFXIFNVQlRZUEU6Tk9ORSBG" +
        "UkFNRVM6NDAgUFJFR0FQOjAgUEdUWVBFOk1PREUxIFBHU1VCOk5PTkUgUE9TVEdBUDowAAAGfAAAbH6N/xgDwFGKL+y/KI5Eu7FutOKrApsinZByXWoP10" +
        "UScoloLwHgRNYYdC2j9TkJuKL0F3FzXLQaBx0wMUJK9elXr9Pn/v7+kZcdKBgDFXRqcw1vDhBSzrd5Rf242r6Ecll5UXzVEF+DrjaIYRZO4peFU56v3l8k" +
        "lsw8XesKNhxRJXhNHf3Tb5s3cdRVSjHoIIW18VAIHZBYmLzep4vdYzPGxrRK3/x8mxIyuOcIOBhVAXRzM3x4XJPDeMPz1GkCM9RVUkSpUpmT6xa0saxD8L" +
        "JtLoJZ8ULMmkZ2ZwOfk7XKCqERBQ6TJLxncY8JqZmRwfB2pgFfrxnukQfdZ0Jmidh+QjaTNy6LjVaMJV9zXDZkfJB43EWsBGgy5ZCbfgVaxYdiK8bwAwzl" +
        "e5z7fQ09AuXvohNs5HDiQwddsaanAwS6LcYBemp8qYvzgB+wOrTQBO8ImlpcRsLSpI0HPRFVLqdlralyr7B/KZ0BFwO5yEeZQK9EuLLEesB5XKDutLXSg7" +
        "8Q7SoEQST2XJwabtQOjhhrOZ2/6HV52hCvqUhfJZkrjysUQZaE2zvsAgVVEt9KGdTsXGOfn7eROhm+XVrzhG5zQwlJYXxIJVayPGXpahSzI9goXPdgJpKv" +
        "EJACDMIIju3BzmIfnu9JHBxKxs/3D+p4fyGV0kxUX2ned/uJ2tiXEbTA3PTszNNokicUGfwe3GOXiAKxHA43MesJoxdXnHJOiFdJBFd/fFPtdJi9WMWU2I" +
        "dScpFhYtMiJEpThjk/szZj6v3JY6LyXHltDKYHok8ag6xhhkKPVRfCogk9qo4njJB82YWJvLbplZs5gfZvp+Mz5KnFtZRu6xiIug38uJR1O1AIP1zeCPms" +
        "oxoZZdDwpC0R0N1Wl2WEmUGWSpXQ8dOi543wRq75X3Tw0NZ7F0wBeaIou/sGInQIYaU4b42Qb6rFc/1nfPCE4uFlyJai6oVds/2rk/WFasKGgBK01XfwHs" +
        "gGEty9UuL+K3MnHxum5L1JEklZPZxe+po94gUwGhBIZzz+2SyRUZ3LnAEVF6gzXXKtEOS+JcPHFrp/UEgZUlcOuS0uRLzEB7kXJDMqq9IqksHy/e4xmTbz" +
        "V9xBC29/X5C8hDBqrduSXV+1wVxcAlR9x+PjbOcj3BrTQPstHG61Aq1ZVAZC/x6uh1dsilQwt0OvAAj0VAZvRQuDnxSGj5wAY7gg1vZ9wN/eZc4mlwitIM" +
        "awsnTR+mJ4B95pYN1Cbak0pF8SkJlFrybuBaDozppAguEWNu+dheGjtbg4Sfm9eArgAHfrYYey9hYwIsjsiP1qjnLYT37dE6pG9wu5UqDIxBQl4dSNZSyN" +
        "bplWOdguKk4AZfd7PKrek49ssDgLuY0uOiXqjspDkeBceJICaKwxAcmwraetMlLGjXs/sRDnQIRscHArZirhqxHB/OU0O+G0AK2NjWkFPTOy5+LTcvV9N6" +
        "Ct6Kw6LBNaPnzxRExw3KxfRqwEeHCPAb6mQu6P8jS/2+rHkPp0B+8CWACX1LOxdqn3kpsfjiGwlfsMi7me4gbhIfRqW5T4Qo22nAGQuYa7awCgsPM1mNfl" +
        "BDZnX1A48v7ICqAWn6/KOzK4QFR45emjRfnUQ8ujhhO5avvs/FiecpmZ1sb1s3IDCQ3ujcHkzsd4g2SlCcatnpWNw2QY76Dq07VFDS/qRYwWKIV/gssrg0" +
        "RMfVGWKSbbrEp31JUP6osDp2oN0W5FjkiK6vZNqPk5zSvhuM3kD/gHQ1Ph+m/IsKUa09shCrSosH2W7BTKQCaa+r2LB3Tj33sKCVbbC4Y66UVRTz5vjT/V" +
        "0WpNabd8d5qJbaCTlRhMVU4w/DdUUiZV6lnKVrfesH80zvupks3BSKOcDmQAFOyelosYeQV1Zr2X0Yc8ShuJDFsF/Do42C35ai0b2ZHQ7G+nO/8PTWELcQ" +
        "oGzNqBRFZ+rO+h+xcqX/szcSlIgsBRxRW/TUSOI/Pd4GIKivklc8DpsYNvRlWGMaZxVPwHVtMKAn72zcQ8CsUdxPby8zQ7tOA/11WRoeLeexllG9Xuozl2" +
        "M+fMMD6GzFXoKZSz9wbsXUd2I7VxMl0HucugLkKZhgIchU3vxdOUFlTseNbI0JnfCoPx6a8zYYi7xuZNZtk/iOzW0lv4jeSAnovun+M3cJm9EOHBa4vpUp" +
        "FLmuwyxwUCSdIVbNY2wXdW/dRybUMCTJ62oPy4rxJJU8RjYBgFo2DkAgAABn8AAGx+jpROz1GSUl4R47N6KsnL2d4TfXm2RIn5L+Gc4AftFRO8Xoepw9ih" +
        "HRVgM1vatYgpEhrKKAqQHmH7J0auHRCOfx2LVC2EHj0qDDJNhXZu+7D5slLbvSagDOK9E+H4vOMxnSgWzyq0zGPfOuWBQgMt+P+q66tGbg6huC+GoaH1ZX" +
        "cAX07dLdJok+XjExx6KSa43vC+q1B8k16LLgwiPvVg1YO/D4mLCMYZLm9DALbah4x7y3MB7s5UpkKp2TkldNiafhNRwRhHOXSp+ZqxxRYqutNrKfuHahk/" +
        "084pHWdDaR9egZ7Eue+izSZX0woJl67B/InT4zn88USRqXfep9Y5fadf11SxLF7RiICT51I+yXe6k62Z4Cfcvdqpqth3dwS53MFk8NJbD8VkXAjgRMygy+" +
        "h0apoYkZn6KMUJ78tET0U6a3eZ77mQEUnyHuZZHjttuHywqf/81xfKHWPrqiwqsTV12zwg+j7dFc3TrySkgwJXiQJX51GCbHGZU5H6TxgJYHU2sp+09ejv" +
        "WpbgY2hhr9hYYrK0cN4FMe53Q99RlAnFTCxFoPhSektmvO0MOfZSK1Sb4AMVJwBg5614E8NZ7KiMKCdrdbX9cemGXHLb0afwVXKeMXVzhfmMCOEdY6alTX" +
        "3wKmiaNzEZA0HAW0EphNOEbJY1v031SZ2LjRrfF+DPP9EJ7XEQRWczPj0ju5tgGe0IZucfOW2LSEWQacDGeGb82Y2aqRaJKGsrA002wWpqRtdjmiXHCjqQ" +
        "El5XpotK1kw2ISK3cZreBZzyCsuPnLOage//t4ETkU07XFHRl0hhr5C1D9FaHWRigB0Ri56ks67/P+dtyC93zjifbYAnMck0/E1YgUdbGg/gfo8SgsO95X" +
        "isP99Bs17a56UR0lvotsyQoHOFUfcypt0gLY+Ob2JrE6c1lP08G2xd/9Nt+pec3bpMV3L1H+3xws1AEw7dPtOvhYhRdiXFHamSRD9AX1aqOSpedMDOPsxH" +
        "DAWYXAemhg95BrqK0dKcPk2WdQAdYfDgdLXDv1OJH0gRRD/w/KCss0j6gALu/GJkpS9bthVJyR1U8ULXdZdkSUOgk3YJgotbuF5LS7TyC22OkSQcnLsB4X" +
        "Z6yU8amTm2zSwHjTnr/Onsxr2cxlqUEMznjwed+rJ19DAYVT2Tx8tMj2Z8hTFJiiDO2l79+1lTOf+0R9uKG74XgTWjbZdi3LTc+Id1vZBLfhj/hiEcrIml" +
        "URpawInNXWtmrOYWegIV/i9tSOy74euK3FMWUSeUFTDvRCotSyqfQXlB1aFldAD3u3fbCSaSyeU0VppXOPACQxn5fq5TVX0BvW6b/CELHDqaPjDuJQMs3v" +
        "m6BX2WHr88DXqKGmvQCCZ4aux9Cwh4ug5oIhJQN9WbHqQulkO6zK89EbCljzOfO/OxVw/G0Fm8AYnR7LO436rJIHHivldB7noxeg24TGhvwPSjZtNP4O6u" +
        "R52FMDmqDgP6pu+MvazVHLncuBIZamIws2wYE32YT7mcWGbmNbm0xYCNyI6m0PURTA9HE1BYPreIox0MY+rkm91r290vVV33hOsmlmoBSSpc73UT861QEL" +
        "NoMGUDOkJSvszn/JZbqUj4U1PcmVWDxMBdwG5Pp//0qRvPahmy0FZmiPbd2Bohz9GiyIXlUtY6iXhrlUL7LcgCN3IteC+uyTx5TSPTgsDXcfrNgoqtqQ7x" +
        "s5iMg6JvkKjy4BjSQ5DzxkRMR9OlWGe4zPDdOec9BM8DA/Qhw4WH8K/UmVYBA0QJoVYyTdVNmcNK4zi5HVxLES+D5kHQYQA2BqaFv2v5O4ACyOBNmSPHID" +
        "bi60nhBNlZN6CoM0Y3Kel6l1GUiYS83kcwCFPTJmYK+c/VlIQ5Wm270n7Qx3s/tkTAmBK3NZbEYiB0TmpqVa1l+3Q17cdrSdvOG3/T22EcWWte6Ml26nM0" +
        "qZQNe/EgkAkiTmeO0foTA4WZIY3pPXmCdEs1HxSxSiiWYmmdxfR+71EKlUG/sq7MhmmEEsr8lLZhjy93USiXBBPfBpT/jxLiHIXatO6fvEh5tzxwAzlbz5" +
        "oceFm5Wq5gpEiPiNPNXGvTsOxdjS90QPa7atz/6VVlWPCTMnoLnT0CQiP7eI8s5eoT9rDFG0U680ORX92K4FVjCtvqGTPoU1zPSWOWr55PPxDjTxNbN9qY" +
        "Ivec9GaSHMFw9YSKFpVI1jc32UWQtEVVUJl5Y2AYBaNg5AIAAAZ9AABsfo+Gh4Qa+8+iRmxB2cwssfwGX05DBaEWQWha2IqNkPs3TzvDAY2teHXVljQOXH" +
        "/nVNv0OCwRsBhHOdym+BqEhE+6SWmkq0/y/zdDUvClgPEWDBdLMqasOJWZAEV1J8XUKmCbZm5klQYU5slP1XuQUrfWsrvxlOG9e2os+0nZCnVO/H0MWm/1" +
        "4aKYWRjq3wBqC3r9Ail3ApLxwM2n4kJhywfcAzMbFkFN/KkXtiaArRHtLPDrZTU/9XSKi0FimhUpCtLy6I8xDvgMDrd7LYdRsdkif3jfEannTRnq2dyUSY" +
        "JlabiAZPoQk4R9QxWmt8h1EDmdw7ikZ22kuy0AHWMnDtPP0bRQZM0CW7ybu4tAmqP946SCDDQgiKFwGYeer2lkHOM+dQJPtZvP5Wjqde3QOBsx7XVP1bBl" +
        "utfaSYbRU70tAUVYZRckr4msXtnt9Qlu0x3Qi+m6+rOqQcbgKBYl2GbQOlEgXY5hDo1boFtNDLP/ZwK4PRn6uXiMe/ihxFtYrHdwp4/WizgJSVse3kFCdA" +
        "/w/ZT6fdDtscljCr/NCawdp3iOLtzT7A3xhRHMIw509i8e7HreZ4MT8tntw+pZXLVg3Vb5IPacnAA3wQN9Um27XE1X+v0I0vpc4ywY6OPJ18JeAuI3B3fN" +
        "SsP2avFgz18NNJ+hgtKm93eKTQ7AZItMceYde+CZq1m56neTh+KyJTyY4H+zUlG/NsmACrRHUdMJR6L9vrr5ldtWEs/Pthw+2s3VgFYSNQx7Vp87bsmqgr" +
        "wCEaZHJcpt2PCXr024ZGebJYMVnAU4dhDGgBxOc+88IueKAA7aqRE4+Jt9X8CWjzWXDsXmsOc2/z6SgG2p3tDyqSNZdmtM78EpQpIrYXk7v8fBkl3qIPQj" +
        "elVIS1bG6USzY4nV0FYl2BsYRUmuRrEiAGKdi5jYIVV3o7Oyo6iQyZkBY0wLNUJXomW9OpnGI8naC5BWtJU69dZOAd/+3tBoWsZsT9bDPqLYVj8GRMhmOx" +
        "09xWoawe7jic2n8rzLVa+VCvh6buYuRFuVylEg811I4bED0UXLUOz2Am1VqC/AFv1NOm30d1ZMm4SwG3HIxSbEXdZiJF/JdOrlG+xfhLtR9ZNttspH3s64" +
        "5tQzb10LxpKXsC7P64pGbmeE/gWeKqbT3Ic5RYfcBi2cYrkob7iIJhuUcTPQHUDWKEdf1Hd9WVyEmKDtrPaK1pao1bQ54nXEjbRvFyjQX2HDU6A3T9ZvnR" +
        "mxl2j9boEqOXsLarPCTt/xfqqubx2T96eqZeSwYCMANbAJmgv/Xr+V1hk0FDxJDzmchm1qLEOE4fZc7FhsVKl4+YfgZ1aBgoXibW2gGiUJ+ivTI6j7uZAB" +
        "iq3kiEsUFrDrULB6788OZiP0QITU2sMNCQ0vToccac8uMPhXgU89mTOCZeHcG0FwPkAeBJPp3g5pBHG1AlFZ++nenaSyVfiMbzUyh50c/YCWb7hGgh5NU0" +
        "zTE6gY6ek0PUVtWysbHU2V13YTALjdJ7JjAy5C13N5GMAyKtjzlaGXvQ5uzA3QZFQX/ovjFzSM9KcEkXJ7gZIoQKj8SHpfG0Q6IOlPxAFLyDhSg1gKeM/q" +
        "B6QEKZReS6+87bv8hjM0Nzd/NdAu68ZbY7nkf8ey4L53SaFTT2/qHYGdW0GHta5+3Gb4+94jtq5SFoEThdobsAMDxEjslRMUWxN5Lu/qfRGLj3YzN9PMlG" +
        "Q7qJrJjPQ1tvs9nvzcUDRGfoFKIKC59WEA8wNDklkZMB7pYujGma4AzRtypbTT23+TzKf3NdPxuG/lc8eWJp8EoxNNEfUZ3L3srF9xPiZV/wJFw7qQ23+b" +
        "/2uvnnNgvKbq/jc+/kNvQ44RkwIr5mpKrQ4vrcg545u9ArCmAnG7CH9PB4BFUa6SO9dHF3mYHX5oNxBMT5TrYxBKYuDzRi47+MCBabWw7ag6NRqsofkII7" +
        "0X2lJcDJu98OIaa4a0xoTUl/FksKwoqyEivcUCfTXPfrjHPSulIhaKm+7xSWWgZZtYttB3QdvihXdDaTuraLlhYqKwQdXad0aRmkZigs+5RRbQfTuKIFiM" +
        "a9h+MWRBw1x0v4y62hhLv3LkFhlFRRUB/Koias06zM8H18+L6KIvx1726/r1rGMhlPQnaXB/BRG9nj3PTqVHbVUwN4820+CbTdxIHvw+l2HCTM3mWGvRVN" +
        "d4fX9jDCCvAQCmueaeyLKBdwBjYBgFo2DkAgAABn8AAGx+kHJvMqGOIWdY0orXnExlygS1mE9c0jHDGZRCI15TzcJIR4GPpLXzIHhA2W5X9tCTboEap9Iv" +
        "NQJlTg0mtSkboSoA9RF60rM6nicZhJopFAMObpXFR7R5LzPdVGqzuHuB011PsG0EEEJcQoVs2bXn4/5pWDVBbVOgc2pXxzjEC27cKJ0U8gwc6uwochbzNo" +
        "Dh0q7EJLDa+Lh8U2/oZ8J1/YPsINv+KueCcWTYFDFVBxxudy0wFanuFPI+U7lG4SUxkH/Z40hLaX7xueYN/uIuV/aCkK2+EhIBH3kMdvfB6vTVYvDSn6ae" +
        "hHo4npfRyhb9LtxAuBftKwC+mkKfEHcddU4Ie+YmmtNyd5d5N8hg6o83k2NOUoG/mXjYUQ5Ar19w/t0+KZC9vKjYKcwbfilI6joQ6K4AupaBNKXjm1ZqGX" +
        "7KAf6Zlcz0qTQP9luxBL3ecPKmpHN9uKRyuRtAZtCClCa4U+hWMif9e1vh3LhUph7KldVrSIJQ+P8Bg/77eXxtJOY4JNzutApTLmk5kZjhMIS6nGTPt0gN" +
        "5+nm564Zl2lfFkUqxHLvPsu+pJFhv6gymLv9EU5trb35pvO5nIIeAWDhqqxq1L9ETibRwAiupi29vXiJqJ1glSOYEnykZMhF0zoIdTSXJUxwcMrdlZJLBa" +
        "0S0B2fQ1yrt6mUTXG1LSrJGluzCfYdyYnPUpL9xZUnmSbFeLhnRcVNO2efpZiEDF5A2ptxRyeljC4unKuWH5HBXNaxKwD194f/IVxoMWK3ygHBNVSX7ECI" +
        "hAhkvYzILCP2YzkLUDepWW8Vl9f+HxL1FI1frFAAnwmCjRDW4DpdWEX6eAwF3St3QNqy2vBEgv82PIHmmIj3PcZuAbdpsZj8cNtIHuET9HNZPH45gQe0RJ" +
        "wFJOUcywTP3sU6L+4uQEluEvmHAC5e9P5eKxC+PzKqW4BrjxefeWrpKJ+GFRSE83JTZADiVunaxzYo3JwXCyB8tRBegeAXmAwT23IK8wFecV32wWuJhRtB" +
        "AyO9izFon1DzeSOKqcJW3y0HUhv745TkZ3A6qyayU1W+056jaL5BT9pkKCkGxjGiawM5xsM9PSi+rTeZJ6iHtXhzkHUDPCKXHa+a/J/8XhyHii+z9SQ8uv" +
        "aDubVx3MfQEC9mBoWUXKBvwsBJOB+T0JVohKpwCGL4e/HOw3rfc5ryaC1yfJn4VB5/0z4xf8TFwJIHc4X9sG4xKPP50Pucui4GmJ0BZz5we/h2OI7On7l8" +
        "bLznrJJ0awnIIkOCx8PQ18fFmO12k79MZacKjgz1z7UwR/Q3+ppBc3ZMI2kalQHCg1FJisJIv8smrOAQYtBdPMs7a3EDM789KdLvwi5tHpD4Wle1+ZuM9k" +
        "XN15vPryjnEDipq2Hje3CRFF6Bpyjj+/rwyhrQ0HLPwhMZx+oAH5SfcdOiYTgCgFzGsi2y+ZTJT5QbKfGPUSEjCArRBi47ZeXXebYJ3yYG0hKLJPB+bMey" +
        "Kv8CYku9eKFyEt61pgF7Q6zAH1MMF5Y0CaTC3ZXyG1u7eok4Ll5E5uIpYdfB/Z/diHODsC1mqZFI3vQTpva6PuKzYAqXOl2L7nIL6BRl0XiCU30B5lWE/0" +
        "+pIXtnmpCx8wUuTHer5ymMVJH9yEDJP39Ryo8Q01IWxnMu/tc+zBICd8CgyQj3o4nCIOS81ASHW0pJrRpEeB2VS8q3jLO1qDhLJfpyD4rSE9L+fK7K7glH" +
        "zN3NGa//rIV0z4phCanzTyAzBoACS2ZZE53Ni29MUymCIGXu5DiV44n9LIkCWhh987tfrEyBaHWT8Whimmo8DJRpqZd/EmIKGJCpGpv/t6Yw6IIHLGtDNb" +
        "I1fIh0qQezfCj0Y6JHj7Z8K32EJGJA7PAYP5Qx/jbTmZmFoz4f6OJ4Iyp2HtzjbCWrsDZZMFM7NFHwRBssoPq8L1vBSUZbOQOi84Z8F27T/vk5xqGCFgtM" +
        "L+TX+QObc1ECz0x28lIWPuaaPO1A7qMqbnzv5QJTaZcKDkTdefytXUuUptcmVdmXfWZW0XdLbT6H3RRktUmQEbR6GPzPOQISBhcbyd9tEEV9RTPGXp+cN7" +
        "n7xFMZiy7zASbnALJaT2xyv92OjeP7/Suvlk6I46bBRzvuD9+XC/iVnXGNNtnSXQnEf9zkOQjG6tp2z7GPn2oWH1hx/dsyxfKAEQcE049+o1XylJqKMoNX" +
        "BjcFvLFBYBeeWdZuY2AYBaNg5AIAAAZ9AABsfpFZMJhp5CXf+TgNjhUtkTB7+MDevTHqkLs6zhIiRI7IZF55iqRb7AuiCCOFh9tGhoCBjjQsF+oezWlQNs" +
        "YQR4SsOX4MQFVViWjb474AUUx7XJFonpcyrplFZnk9pJYP15QJaMTeohz1uMQ8ujjGMnGq8iacBrhzdeBO/nTUkPyQqKy5BgNFrwwXbB5MBi0Gzuw0ynZq" +
        "qhbSkS6euxYa1TjwoTZr4qKrOi8zGFhe1PnV/MJy8lJugdqQwgMoeWvnS33AAskk2jxwVOoHADLV0yJXuzNwkxzjTTLyAkFPQ4BK5JSbCS9LGLr+U0DIuo" +
        "fkEwVehRkxy4BCX4U10rxjRyCWshpUCpgXvJ7RHGuhBo0EHr+dn21Qr5PrdAw9yQlfIBjGWYq7y2sl41fg4I+brDdwbmSigdnCtlz6JBsXOaadgYixt2Yi" +
        "J8dmVTGxFOi21IFuMXPu9HRAT14NBWLUZvH2UXDt4pscjSe5bYWOfvSqVCHZAdYbxxi5jB5zIhnkguQneuOrGBnhcytbCkUUvSVufXd1iZsJfZhamI4O48" +
        "cPCryLxaXbNSuKhEL09wp7T8W9S2Ee9DKYJ8LT/3KKoPegJ7gzeodzsVkB6P3SChGvOAmRIKQ4rpUMz8WFLZPNnW4dMrckwNvtuaTxZVRfl/KU62GPorW8" +
        "kAL6J1mc8VQ99+EIHdGT08dc+c0npA0Nas3JAZ8IMtEN035MABN8VFnhS9n6/lWZWRGCx1wVMe2Qsp/JsDK2WfIqw0U8G95vfm3b2s9pN1r9fQOjTdqLd8" +
        "6rGUax4PuyTmnrUbj83qM5TF81QShnf+ldkbGB8DM9f+LvQnqX9qPPRNCG1ODoGui8vYAgikVdTt4Nkelp0INdbH/7LEXhDTAYqMlPUyJCJynT8GjoLPZe" +
        "pmwy5O6Xs73ADjKVErCr9hzyt6dI8HF+wnEzr6kn9EX7H7MoOx/T68NuVsYMozxasdU7i2ayLME9CMJJHez+w0z3M+ATq1Gll7OSZomi5GN6sH3TDG0wc7" +
        "v1l/O25VjKt3z/K6K1CPulazb8KenrVsx54VdJ2Bh0rXWTNtFfN+9lZEbnRquRYxBW+oNA5VCNfzO9Ksj0o+Z61Pxy6Rzku5thQXkvn38pT57oP77FNq0v" +
        "mshSFPp1ID2ywamU2QXrSfSsN9ffy98/PnshXlB7BH8KlwHo0pV6gwJG0Wn+WUi5sReRzQYBe1C6+ynk2xrYgCvnHFp6uEAKaOQIsL1rfN4V00MLcWwuR0" +
        "4KyuZr826xRU973uBdYdZKqtybucOoAdhHE+j9PaSSE40PoNH5PCsmM7e6xau9J2OFMk5ij0NUeRn+YsCw3QrHB9g2MjvFAF8B0rspndYWo9ypiJHRFnHE" +
        "YrPY3MnNzkILzcXgZ04t9OxTzksINhe3ss7djQkNAJWg/qeO1MIiDVZt7Z0g+KsTbupR1vw1m/a7DbhB5hEOClDH68beP2tZK+YIag/VL+mmM0c03WxipK" +
        "4+/u93ZBYlZduLSU7ZCwo3B5fhIIdUbeXBeuXHs33pq8i/L4M/K1xN+CAbSvoXe743GUCqXVILxTDxg62DzPFJWV2dhRRQy9nmbhzaUQ9W6JALgn2Omokg" +
        "BGcAxKni3XpBGT7C5pr02LBzVpqy/WYaLnQVK3PBHuRZvRjyE+scf9KbqmRrvKP4YhmWArLEWqUr7v5pzRJyLg9ULwg9gmTB9UfvbdrNsqbdScht4MrBf7" +
        "O6fsz3riPs/sXEhPJ8uoAAILa8pSBW3dv35bVsaRwS9OlgZlvOIpQrh7tg5JZdBv+CZva92zOnDnJLvmMiMf59gqzidV9NmsbPwWqo5j1YQwogUagbzbqi" +
        "KLb8Ldn9SeELFNV34uE0PNiJqpVhuX9QacKQjyF6hOGGX4qLVNjtrSB0f0piTmCQ/6+Dx1RCh9hZmNBzhVa9zg1mY2XXNHOLLnZpprFS6EKywZfkKHneXk" +
        "Uy6WTDbsiILysQt0UIP02jPBi5VPyarIl1/7wqVONuWbw6sYWLHCRQp5eeietLmyse/brKYcMYaq2vuWs+7T0yLdxbvnPAVcIj8b7DEETtbfzgfa2JjYt8" +
        "iaxTjVIOMEVtn8FkYGEW2d/UxbflCIUmKO326WsljmyW2u+Gjh+tdVB3dE1wlAk1o9ocIiEyND8vAXirQrccBCXWYq0H8+DinjKmmwdXcoVG2Sm7+WEnj0" +
        "r2TGZjYBgFo2DkAgAAAAAYAAAAAADlHeYLAAAAABEQEREFtEZohotKW9EyoVosXldE9niA";

    private const string ChildB64 =
        "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" +
        "77DAAAABEQMhACEBphmL5I";

    private static byte[] Parent() => System.Convert.FromBase64String(ParentB64);
    private static byte[] Child() => System.Convert.FromBase64String(ChildB64);

    [Fact]
    public void A_child_chd_without_its_parent_is_declined()
    {
        var ex = Assert.Throws<ChdFormatException>(() => ChdExtractor.ExtractCd(Child()));
        Assert.Contains("parent", ex.Message);
    }

    [Fact]
    public void A_child_chd_with_its_parent_extracts_and_verifies()
    {
        var r = ChdExtractor.ExtractCd(Child(), Parent());
        Assert.True(r.Verified);
        Assert.Equal(40 * 2352, r.Bin.Length);
    }
}
